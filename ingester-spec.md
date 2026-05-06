# AIMemory Ingestor Specification
Version: 0.2
Status: Active
Author: Eric Paul
Date: 2026-03-10

---

# 1. Overview

AIMemory Ingestor is a local agent that captures LLM tool activity (sessions, prompts, responses, tool calls, artifacts, and relevant metadata) from CLI-based assistants and related tools, and asynchronously pushes normalized events into AIMemory. It also indexes local code repositories by scanning source files, parsing symbols, and emitting code events.

This is designed to avoid placing AIMemory on the critical path of interactive workflows. The ingestor must not materially impact latency of Claude Code, Codex CLI, or other tooling.

Primary sources (current):
- Claude Code (CLI)
- Codex CLI
- Local code repositories (via Code adapter)

Planned/possible sources (later):
- "pi agent" (if it produces local logs/transcripts)
- Google Gemini CLI/tools (if logs/transcripts exist or can be exported)
- Any future tools that emit transcripts or structured logs

---

# 2. Goals

- Asynchronously ingest prompts/sessions without slowing down CLI tools.
- Normalize different tool log formats into a common AIMemory event model.
- Index local code repositories by scanning files and parsing symbols.
- Provide reliable, idempotent ingestion with checkpoints and deduplication.
- Support Windows-first operation (with optional Unix deployment later).
- Include configurable redaction/scrubbing of sensitive information (API keys, secrets).
- Allow future extension via pluggable source adapters.
- Track machine name on all ingested records.

---

# 3. Non-Goals (v0.2)

- Real-time proxying of LLM requests (gateway approach is out-of-scope for ingestor).
- Full IDE integrations (VS Code extension, etc.).
- Deep semantic interpretation of prompts or code beyond what is needed for metadata extraction.
- Perfect secret detection (provides configurable redaction and best-effort scrubbing).

---

# 4. Operating Model

Ingestor runs as:
- A long-running background service (recommended), or
- An on-demand CLI command (manual runs), or
- A scheduled task (Windows Task Scheduler) for periodic ingestion.

Ingestor watches one or more source directories for:
- JSONL session transcripts (Claude Code, Codex CLI)
- Source code files (for the Code adapter)

Ingestor reads new data incrementally, transforms it, optionally redacts it, then pushes it to AIMemory API in batches.

Ingestor maintains local checkpoints to ensure:
- No double-ingestion
- Safe restarts
- Robust recovery after network outages

---

# 5. Deployment Targets

Primary: Windows
- Runs where Claude Code/Codex tools are executed.
- Uses Windows filesystem watchers or periodic scans.
- Stores checkpoints in a local folder (e.g., `%APPDATA%\AIMemory\Ingestor\`).

Secondary: Unix (optional future)
- Same behavior with paths under `~/.config/aimemory/ingestor/` (or similar).

---

# 6. High-Level Architecture

Components:
- Source Adapters (per tool / per source type)
- File Watcher / Scanner
- Checkpoint Store
- Redaction Pipeline
- Normalizer (AIMemory event mapping)
- Batcher + Retry Queue
- AIMemory API Client

Flow:
1. Detect new or updated transcript/log/source data.
2. Read only new or changed records since last checkpoint.
3. Parse source-specific record format.
4. Normalize into AIMemory event(s).
5. Apply redaction rules (optional but recommended for transcript sources).
6. Send batch to AIMemory API with idempotency keys.
7. Persist checkpoints only after successful ingest.

---

# 7. Inputs and Sources

## 7.1 Source: Codex CLI

Expected capabilities:
- Session transcripts stored locally (often JSONL).
- Operational logs stored locally.

Discovery strategy:
- Detect standard directories if present.
- Allow explicit configuration of log/session directories.

Possible default paths (subject to confirmation per installation):
- `%USERPROFILE%\.codex\sessions\`
- `%USERPROFILE%\.codex\log\`
- Optional override via config `CODEX_HOME` or equivalent.

Minimum data to capture:
- Session identifier (from filename/metadata)
- Timestamp
- Role (user/assistant/system/tool)
- Message content
- Model/provider info if present
- Token usage/cost if present
- Tool events if present

## 7.2 Source: Claude Code (claude-code)

Expected capabilities:
- Local transcripts/logs (JSONL).
- Includes tool events and file operations.

Discovery strategy:
- Detect standard directories if present.
- Allow explicit configuration of transcript/log directories.

Possible default paths (subject to confirmation per installation):
- `%USERPROFILE%\.claude\`
- `%APPDATA%\Claude\` (if applicable)
- Optional override via env/config.

Minimum data to capture:
- Session identifier
- Timestamp
- Role
- Message content
- Model/provider info if present
- Tool events if present

## 7.3 Source: Code Repositories (Code Adapter)

The Code adapter scans local source directories and emits `CodeFileUpsert` and `CodeSymbolBatch` events into AIMemory.

WatchPaths are treated as repository roots. The repository name defaults to the folder basename.

Supported file types: C# (`.cs`), Python (`.py`), TypeScript (`.ts`, `.tsx`), Go (`.go`).

Change detection uses a three-tier strategy (see section 8.4).

Symbol parsing produces name, qualified name, kind, signature, start/end line, and start/end byte for each declaration.

Machine name is automatically set from `Environment.MachineName`.

## 7.4 Other Sources (Future Adapters)

- pi agent (if it produces logs)
- Gemini CLI or other Google tooling
- Any tool that can export a transcript (JSONL/JSON/Markdown)

Requirement:
- Each adapter must specify a file discovery mechanism and parsing logic.

---

# 8. Source Adapter Interface

Each adapter must implement:

```
DiscoverFiles(config)         -> IEnumerable<string> candidate file paths
ReadNewRecords(file, checkpoint) -> IEnumerable<RawRecord> + new checkpoint
ParseRecord(raw)              -> IEnumerable<IngestEvent>
```

### RawRecord

```
FilePath      string
Offset        long     byte offset or line number
Line          string   raw line content (may be empty for whole-file adapters)
Source        string   adapter name, e.g., "code-index"
ContentHash   string   SHA-256 hex of file content (required for Code adapter; optional for others)
```

`ContentHash` on `RawRecord` is required for the Code adapter. The hash is used as the idempotency key suffix and stored in the checkpoint to detect touch-only file changes.

For line-oriented adapters (Claude Code, Codex), `Offset` is the byte or line position within the transcript file.

Adapters should be resilient to partial writes.

---

# 9. Three-Tier Change Detection (Code Adapter)

The Code adapter avoids unnecessary re-parsing using three successive checks:

**Tier 1 — mtime + size check**

If `checkpoint.FileMtime` and `checkpoint.FileSize` match the file's current `LastWriteTimeUtc` and `Length` (within 1 second), skip the file entirely. This handles the common case where nothing has changed.

**Tier 2 — Content hash check**

If mtime or size has changed, read the file and compute a SHA-256 hash. If `checkpoint.ContentHash` matches the computed hash, skip the file. This handles touch-only changes (e.g., a build tool updating mtime without modifying content).

**Tier 3 — Actual content change**

If the hash differs, cache the content and yield a `RawRecord` for parsing. The content is held in a `ConcurrentDictionary` keyed by file path and consumed by `ParseRecord` in the same processing cycle.

---

# 10. Normalized Event Model

Ingestor converts all source records into one of:

1. SessionUpsert
2. MessageAppend
3. ToolCallAppend
4. ArtifactAppend
5. CodeFileUpsert
6. CodeSymbolBatch
7. RunTelemetry (optional, for costs/latency if not captured on messages)

## 10.1 SessionUpsert

Fields:
- sessionExternalId (string, from source)
- title (string, derived or configured)
- project (string, optional)
- repo (string, optional)
- branch (string, optional)
- tags (string[])
- startedAt (timestamp, optional)
- updatedAt (timestamp, optional)
- source (string, e.g., "codex-cli", "claude-code")

## 10.2 MessageAppend

Fields:
- sessionExternalId
- messageExternalId (string, stable id if available)
- role (system|user|assistant|tool)
- content (string)
- createdAt (timestamp)
- provider (string, optional)
- model (string, optional)
- tokenIn (int, optional)
- tokenOut (int, optional)
- costUsd (decimal, optional)
- latencyMs (int, optional)
- raw (json, optional raw record)

## 10.3 ToolCallAppend

Fields:
- sessionExternalId
- toolCallExternalId (string)
- toolName (string)
- argumentsJson (json)
- resultJson (json)
- createdAt (timestamp)
- raw (json, optional)

## 10.4 ArtifactAppend

Fields:
- sessionExternalId
- artifactExternalId (string)
- type (file|snippet|link|diff|commit|other)
- pathOrUrl (string)
- hash (string, optional)
- metadataJson (json)
- createdAt (timestamp)

## 10.5 CodeFileUpsert

Fields:
- repositoryName (string)
- sourceType (string, "local")
- sourcePath (string, absolute path to watch root)
- filePath (string, relative to watch root, forward slashes)
- language (string, e.g., "cs", "py", "ts", "go")
- fileSize (long)
- contentHash (string, SHA-256 hex)
- machineName (string, from `Environment.MachineName`)

Idempotency key format: `code|{absoluteFilePath}|{contentHash}`

## 10.6 CodeSymbolBatch

Fields:
- repositoryName (string)
- filePath (string, relative)
- symbols: list of CodeSymbolEvent

### CodeSymbolEvent

Fields:
- symbolKey (`filepath::QualifiedName#kind`)
- name (string)
- qualifiedName (string)
- kind (string: function, class, method, interface, struct, property, enum)
- signature (string)
- startLine (int)
- endLine (int)
- startByte (long)
- endByte (long)
- parentSymbolKey (string, nullable)

Idempotency key format: `code-symbols|{absoluteFilePath}|{contentHash}`

---

# 11. Idempotency and Deduplication

Ingestor must generate stable idempotency keys for each ingested record.

Recommended key scheme for transcript adapters:
- `{source}|{sourcePath}|{recordOffset}`

For the Code adapter:
- `code|{absoluteFilePath}|{contentHash}`
- `code-symbols|{absoluteFilePath}|{contentHash}`

AIMemory API enforces uniqueness on idempotency key per source.

Ingestor must tolerate:
- Partial files (still being written)
- File rotation
- Reprocessing after crash

---

# 12. Checkpointing

Checkpoint storage is local, per sourcePath.

Checkpoint fields:
- sourcePath
- lastProcessedOffset (line number or byte offset; for line adapters)
- lastProcessedTimestamp (optional)
- fileSize (last observed file size)
- fileMtime (last observed mtime)
- contentHash (SHA-256 of last processed file content; used by Code adapter)
- lastSeenAt

Behavior:
- If fileSize or mtime indicates file truncation or rotation, reset safely.
  - Prefer "resume from last known offset if present", else restart from 0.
- For the Code adapter, the checkpoint stores contentHash; mtime/size serve as the fast-path cache.
- Checkpoints only advance after successful AIMemory API ingestion.

Storage locations:
- Windows: `%APPDATA%\AIMemory\Ingestor\checkpoints.json` (or SQLite)
- Unix: `~/.config/aimemory/ingestor/checkpoints.json` (or SQLite)

---

# 13. Machine Name Tracking

All ingest batches include a `machineName` field set from `Environment.MachineName` (auto-detected).

The machine name is stored on:
- `sessions.MachineName` (via SessionUpsert)
- `ingestion_log.MachineName` (on every ingested event)
- `CodeFileUpsertEvent.MachineName`

This allows filtering and attribution when multiple machines ingest into the same AIMemory instance.

---

# 14. Redaction and Sensitive Data Scrubbing

AIMemory Ingestor must support optional redaction of sensitive data before sending to AIMemory. Redaction applies to transcript adapters; it is not applied to code symbol metadata.

This should be configurable per environment and per source.

## 14.1 Redaction Modes

- off: no scrubbing
- basic: pattern-based masking
- aggressive: pattern-based + heuristics + file-based exclusions

Default for internet-accessible AIMemory: basic (at minimum).

## 14.2 Target Data to Redact (Examples)

- API keys (OpenAI, Anthropic, OpenRouter, Gemini, AWS keys)
- Bearer tokens
- Private keys / certificates (PEM blocks)
- Connection strings containing passwords
- Common secret env vars:
  - OPENAI_API_KEY
  - ANTHROPIC_API_KEY
  - OPENROUTER_API_KEY
  - GOOGLE_API_KEY
  - AWS_ACCESS_KEY_ID
  - AWS_SECRET_ACCESS_KEY
  - AZURE_* secrets

## 14.3 Redaction Strategy (v0.2)

- Regex-based replacements on message content and raw JSON.
- Configurable patterns list.
- Replacement format: `[REDACTED]` or `[REDACTED:TYPE]`
- Record redaction metadata:
  - redactionApplied: true/false
  - redactionRulesVersion: string

Important:
- Do not attempt perfect detection.
- Provide an extensible rule set.
- Provide a test mode that shows what would be redacted without uploading.

## 14.4 Exclusions

Ingestor should support exclusion rules to avoid uploading content from:
- Certain directories (e.g., containing `.env`, `secrets`, `private`)
- Certain filenames/extensions (e.g., `.pem`, `.pfx`, `.key`)
- Certain message types (tool events involving file reads of secrets)

Exclusion behavior options:
- skip entirely
- upload metadata only (no content)
- upload redacted content

---

# 15. Transport and Reliability

## 15.1 AIMemory API Contract (Batch Ingest)

Endpoint: `POST /api/ingest/batch`

Body:
```json
{
  "clientId": "my-machine-01",
  "source": "claude-code",
  "machineName": "MY-MACHINE",
  "events": [
    {
      "type": "MessageAppend",
      "idempotencyKey": "claude-code|C:\\path\\to\\transcript.jsonl|1024",
      "payload": { ... }
    }
  ]
}
```

Response:
```json
{
  "total": 5,
  "succeeded": 4,
  "duplicates": 1,
  "failed": 0,
  "results": [
    { "idempotencyKey": "...", "status": "ok" },
    { "idempotencyKey": "...", "status": "duplicate" }
  ]
}
```

## 15.2 Retry

- Network errors: retry with exponential backoff.
- Partial failures: retry only failed events.
- Maintain a small local outbox queue on disk for reliability.

Outbox:
- Stores unsent batches or individual events.
- Cleared after server ack.

---

# 16. Performance Requirements

- Ingestor must not block interactive CLI workflows.
- File watching and scanning must be low overhead.
- Default batch size: 50 to 200 events per request (configurable).
- Default max upload interval: 1 to 5 seconds when backlog exists (configurable).
- Backpressure: if AIMemory API unavailable, store events locally and pause upload.

---

# 17. Configuration

Config file: `aimemory.ingestor.json`

Example fields:
- aiBrainApiBaseUrl
- apiKey
- clientId
- sources:
  - name
  - enabled
  - adapterType (ClaudeCode, CodexCli, Code)
  - watchPaths[]
  - filePatterns[]
- scanIntervalSeconds
- batchSize
- redaction:
  - mode
  - rulesFile
  - exclusions
- outbox:
  - path
  - maxSizeMb

Environment variables override config:
- `AIMEMORY_API_URL`
- `AIMEMORY_API_KEY`
- `AIMEMORY_CLIENT_ID`

---

# 18. Observability

Ingestor logs:
- ingestion rate
- backlog size
- last successful upload time
- redaction applied counts
- parse errors (with safe truncation)
- file discovery counts
- code index: files scanned, symbols emitted, files skipped (no change)

Optional:
- metrics endpoint (localhost) for counters and health.

Health states:
- Healthy: ingesting and uploading
- Degraded: ingesting but cannot upload (outbox growing)
- Error: cannot read logs or configuration invalid

---

# 19. Security Requirements

- AIMemory API must require auth (`code` or `ingest` scope minimum).
- Ingestor stores API keys securely (env var recommended).
- Ingestor should avoid logging raw secrets.
- Support "redaction test mode" before enabling uploads.
- Optionally support TLS pinning or known CA trust constraints.

---

# 20. Acceptance Criteria (v0.2)

1. Ingestor can run on Windows and ingest at least one Claude Code transcript source.
2. Ingestor can ingest Codex CLI transcripts if present and configured.
3. Ingestor is restart-safe and idempotent (no duplicates).
4. Ingestor supports redaction basic mode with configurable patterns.
5. Ingestor uploads batches to AIMemory API with retries and local outbox.
6. Ingested sessions and messages are searchable in AIMemory.
7. Code adapter detects changed files using three-tier detection (mtime+size, content hash, parse).
8. Code adapter emits CodeFileUpsert and CodeSymbolBatch events for supported languages.
9. Machine name is automatically detected and attached to all ingested records.

---

# 21. Open Questions (to resolve during implementation)

- Exact default transcript/log paths for claude-code and codex on Windows in target installs.
- Exact record format differences per tool version.
- Whether "pi agent" emits logs and where.
- Gemini tooling: which client and what it logs.
- Redaction rules: initial pattern list and aggressiveness.
- Whether to store raw records in AIMemory, and if so how to encrypt/compress them.
- Whether code adapter should also index file content (full text) for `/api/code/search/text` queries, or rely on the direct indexer path.

---
