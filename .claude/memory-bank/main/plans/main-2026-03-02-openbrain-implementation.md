# OpenBrain Implementation Plan
**Branch:** main
**Date:** 2026-03-02
**Status:** EXECUTED — All phases complete, build succeeds
**Author:** Eric Paul + Claude

---

## Executive Summary

This plan implements the complete OpenBrain system: a personal LLM work ledger comprising a PostgreSQL database, a .NET 10 Minimal API, a Windows Ingestor service, and an MCP server. The system captures AI interactions from Claude Code and Codex CLI, stores them durably, and makes them searchable.

---

## Environment Confirmed

| Component | Version | Path/Notes |
|-----------|---------|------------|
| .NET SDK | 10.0.102 | `dotnet` on PATH |
| PostgreSQL | 18.1 | `C:\Program Files\PostgreSQL\18\bin` |
| OS | Windows 11 Pro | Primary dev machine |
| pgvector | NOT available | Phase 2 — install separately later |
| pg_trgm | 1.6 | Available — used for trigram similarity |
| uuid-ossp | 1.1 | Available — UUID generation |
| pgcrypto | 1.4 | Available — hashing |
| Claude Code transcripts | JSONL | `~/.claude/projects/*/uuid.jsonl` |
| Codex CLI history | JSONL | `~/.codex/history.jsonl` |
| Codex CLI logs | Text | `~/.codex/log/codex-tui.log` |

---

## Solution Structure

```
AIMemory/
├── spec.md                          # OpenBrain API spec
├── ingester-spec.md                 # Ingestor spec
├── src/
│   ├── OpenBrain.sln                # Solution file
│   ├── OpenBrain.Models/            # Shared models, DTOs, events
│   ├── OpenBrain.Data/              # EF Core DbContext, migrations, repos
│   ├── OpenBrain.Api/               # .NET Minimal API (HTTP endpoints)
│   ├── OpenBrain.Ingestor/          # Windows background service + CLI
│   └── OpenBrain.Mcp/               # MCP Server (thin wrapper)
├── tests/
│   ├── OpenBrain.Tests.Unit/        # Unit tests
│   └── OpenBrain.Tests.Integration/ # Integration tests (requires DB)
├── scripts/
│   ├── db-init.sql                  # Database + extension setup
│   └── db-seed.sql                  # Optional seed data
├── docker/
│   └── Dockerfile                   # API container build
├── logs/                            # NLog output
└── .claude/                         # RIPER workflow config
```

---

## Phase 1: Database Setup
**Estimated Steps: 1.1 – 1.7**

### 1.1 Create PostgreSQL Database

```sql
CREATE DATABASE openbrain
    ENCODING 'UTF8'
    LC_COLLATE 'en_US.utf8'
    LC_CTYPE 'en_US.utf8';
```

### 1.2 Enable Extensions

```sql
\c openbrain
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";
CREATE EXTENSION IF NOT EXISTS "unaccent";
```

### 1.3 Create Core Tables

```sql
-- Sessions
CREATE TABLE sessions (
    session_id     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    external_id    TEXT UNIQUE,           -- Source system ID for dedup
    title          TEXT NOT NULL,
    project        TEXT,
    repo           TEXT,
    branch         TEXT,
    tags           TEXT[] DEFAULT '{}',
    source         TEXT,                  -- 'claude-code', 'codex-cli', etc.
    is_archived    BOOLEAN DEFAULT FALSE,
    created_at     TIMESTAMPTZ DEFAULT NOW(),
    updated_at     TIMESTAMPTZ DEFAULT NOW()
);

-- Messages
CREATE TABLE messages (
    message_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id       UUID NOT NULL REFERENCES sessions(session_id),
    external_id      TEXT,                -- Source message ID for dedup
    role             TEXT NOT NULL CHECK (role IN ('system','user','assistant','tool')),
    content          TEXT NOT NULL,
    provider         TEXT,
    model            TEXT,
    request_id       TEXT,
    token_in         INT,
    token_out        INT,
    cost_usd         NUMERIC(10,6),
    latency_ms       INT,
    content_tsvector TSVECTOR,
    created_at       TIMESTAMPTZ DEFAULT NOW()
);

-- Tool Calls
CREATE TABLE tool_calls (
    tool_call_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id     UUID NOT NULL REFERENCES sessions(session_id),
    external_id    TEXT,
    tool_name      TEXT NOT NULL,
    arguments_json JSONB,
    result_json    JSONB,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

-- Artifacts
CREATE TABLE artifacts (
    artifact_id    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id     UUID NOT NULL REFERENCES sessions(session_id),
    external_id    TEXT,
    type           TEXT NOT NULL,
    path_or_url    TEXT,
    hash           TEXT,
    metadata_json  JSONB,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

-- Ingestion tracking (idempotency)
CREATE TABLE ingestion_log (
    idempotency_key TEXT PRIMARY KEY,     -- "{source}|{sourcePath}|{recordOffset}"
    event_type      TEXT NOT NULL,
    source          TEXT NOT NULL,
    source_path     TEXT NOT NULL,
    record_offset   BIGINT NOT NULL,
    status          TEXT DEFAULT 'ok',
    created_at      TIMESTAMPTZ DEFAULT NOW()
);
```

### 1.4 Create Indexes

```sql
-- Sessions
CREATE INDEX idx_sessions_project ON sessions(project);
CREATE INDEX idx_sessions_repo ON sessions(repo);
CREATE INDEX idx_sessions_created_at ON sessions(created_at);
CREATE INDEX idx_sessions_source ON sessions(source);
CREATE INDEX idx_sessions_external_id ON sessions(external_id);
CREATE INDEX idx_sessions_tags ON sessions USING GIN(tags);

-- Messages
CREATE INDEX idx_messages_session_id ON messages(session_id);
CREATE INDEX idx_messages_provider ON messages(provider);
CREATE INDEX idx_messages_model ON messages(model);
CREATE INDEX idx_messages_created_at ON messages(created_at);
CREATE INDEX idx_messages_fts ON messages USING GIN(content_tsvector);
CREATE INDEX idx_messages_external_id ON messages(external_id);

-- Tool Calls
CREATE INDEX idx_tool_calls_session_id ON tool_calls(session_id);
CREATE INDEX idx_tool_calls_tool_name ON tool_calls(tool_name);

-- Artifacts
CREATE INDEX idx_artifacts_session_id ON artifacts(session_id);
CREATE INDEX idx_artifacts_type ON artifacts(type);

-- Ingestion Log
CREATE INDEX idx_ingestion_source ON ingestion_log(source);
CREATE INDEX idx_ingestion_source_path ON ingestion_log(source_path);
```

### 1.5 Create FTS Trigger

```sql
CREATE OR REPLACE FUNCTION messages_tsvector_trigger() RETURNS trigger AS $$
BEGIN
    NEW.content_tsvector := to_tsvector('english', COALESCE(NEW.content, ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_messages_tsvector
    BEFORE INSERT OR UPDATE ON messages
    FOR EACH ROW
    EXECUTE FUNCTION messages_tsvector_trigger();
```

### 1.6 Create Application User

```sql
CREATE USER openbrain_app WITH PASSWORD '<generate-secure-password>';
GRANT CONNECT ON DATABASE openbrain TO openbrain_app;
GRANT USAGE ON SCHEMA public TO openbrain_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO openbrain_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO openbrain_app;
```

### 1.7 Create `scripts/db-init.sql`

Consolidate steps 1.1–1.6 into a single idempotent SQL script.

---

## Phase 2: .NET Solution & Shared Models
**Steps: 2.1 – 2.6**

### 2.1 Create Solution Structure

```bash
mkdir -p src tests
cd src
dotnet new sln -n OpenBrain
dotnet new classlib -n OpenBrain.Models -f net10.0
dotnet new classlib -n OpenBrain.Data -f net10.0
dotnet new webapi -n OpenBrain.Api -f net10.0 --no-openapi
dotnet new worker -n OpenBrain.Ingestor -f net10.0
dotnet new webapi -n OpenBrain.Mcp -f net10.0 --no-openapi
dotnet sln add OpenBrain.Models OpenBrain.Data OpenBrain.Api OpenBrain.Ingestor OpenBrain.Mcp
cd ../tests
dotnet new xunit -n OpenBrain.Tests.Unit -f net10.0
dotnet new xunit -n OpenBrain.Tests.Integration -f net10.0
cd ../src
dotnet add OpenBrain.Data reference OpenBrain.Models
dotnet add OpenBrain.Api reference OpenBrain.Data OpenBrain.Models
dotnet add OpenBrain.Ingestor reference OpenBrain.Models
dotnet add OpenBrain.Mcp reference OpenBrain.Models
```

### 2.2 OpenBrain.Models — Entity Classes

Create entity classes matching the database schema:

- `Session.cs` — UUID PK, Title, Project, Repo, Branch, Tags, Source, IsArchived, timestamps
- `Message.cs` — UUID PK, SessionId FK, Role, Content, Provider, Model, token/cost fields, timestamps
- `ToolCall.cs` — UUID PK, SessionId FK, ToolName, ArgumentsJson, ResultJson, timestamps
- `Artifact.cs` — UUID PK, SessionId FK, Type, PathOrUrl, Hash, MetadataJson, timestamps
- `IngestionLogEntry.cs` — IdempotencyKey PK, EventType, Source, SourcePath, RecordOffset, Status

### 2.3 OpenBrain.Models — DTOs

Request/Response DTOs (separate from entities):

- `CreateSessionRequest.cs` / `SessionResponse.cs`
- `AppendMessageRequest.cs` / `MessageResponse.cs`
- `AppendToolCallRequest.cs` / `ToolCallResponse.cs`
- `SearchRequest.cs` / `SearchResult.cs`
- `BatchIngestRequest.cs` / `BatchIngestResponse.cs` (per ingester-spec section 12)
- `IngestEvent.cs` — type discriminator + payload

### 2.4 OpenBrain.Models — Ingestor Events

Normalized event types per ingester-spec section 8:

- `SessionUpsertEvent.cs`
- `MessageAppendEvent.cs`
- `ToolCallAppendEvent.cs`
- `ArtifactAppendEvent.cs`

### 2.5 NuGet Packages

**OpenBrain.Data:**
- `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Microsoft.EntityFrameworkCore.Design` (tools)

**OpenBrain.Api:**
- `NLog.Web.AspNetCore`
- `Polly`
- `Polly.Extensions.Http`

**OpenBrain.Ingestor:**
- `Microsoft.Extensions.Hosting`
- `NLog.Extensions.Hosting`
- `Polly`
- `System.Text.Json`

**Test projects:**
- `Moq` or `NSubstitute`
- `FluentAssertions`
- `Microsoft.EntityFrameworkCore.InMemory`

### 2.6 NLog Configuration

Standard `nlog.config` with daily rolling file targets to `logs/` with 7-day retention.

---

## Phase 3: Data Access Layer (OpenBrain.Data)
**Steps: 3.1 – 3.5**

### 3.1 OpenBrainDbContext

EF Core DbContext with:
- `DbSet<Session> Sessions`
- `DbSet<Message> Messages`
- `DbSet<ToolCall> ToolCalls`
- `DbSet<Artifact> Artifacts`
- `DbSet<IngestionLogEntry> IngestionLog`
- Fluent configuration for indexes, constraints, JSONB columns
- PostgreSQL-specific: `UseNpgsql`, array types for `Tags`

### 3.2 Repository Interfaces

- `ISessionRepository` — CRUD, search, archive
- `IMessageRepository` — Append, list by session
- `IToolCallRepository` — Append, list by session
- `IArtifactRepository` — Append, list by session
- `ISearchRepository` — FTS search across messages
- `IIngestionRepository` — Idempotency check, log ingestion

### 3.3 Repository Implementations

Concrete EF Core implementations with:
- Polly retry policies for transient PG errors
- NLog logging
- Batch insert support for ingestor

### 3.4 Search Implementation

Full-text search using `content_tsvector`:

```csharp
// ts_rank with plainto_tsquery for weighted relevance
// ts_headline for snippet extraction
// Optional: pg_trgm similarity for fuzzy matching
```

Filter parameters: `project`, `repo`, `source`, `dateFrom`, `dateTo`, `limit`

### 3.5 EF Core Migrations

Generate initial migration from DbContext configuration. Use `dotnet ef` tooling.

---

## Phase 4: OpenBrain API
**Steps: 4.1 – 4.10**

### 4.1 Program.cs — Service Registration

- Register DbContext with connection string from config/env
- Register repositories via DI
- Register NLog
- Register Polly policies
- Configure CORS (for future use)
- Add API key authentication middleware

### 4.2 API Key Authentication Middleware

Simple middleware:
- Read `X-API-Key` header
- Compare against configured key(s) — stored in env var `OPENBRAIN_API_KEY`
- Return 401 if missing/invalid
- Skip auth for health endpoint

### 4.3 Endpoint: POST /sessions

Create a new session. Returns `{ sessionId }`.

### 4.4 Endpoint: GET /sessions/{sessionId}

Get session with ordered message history. Optional `?limit=100` parameter.

### 4.5 Endpoint: POST /sessions/{sessionId}/messages

Append a message to a session. Validates session exists.

### 4.6 Endpoint: POST /sessions/{sessionId}/toolcalls

Append a tool call record. Validates session exists.

### 4.7 Endpoint: POST /sessions/{sessionId}/artifacts

Append an artifact. Validates session exists.

### 4.8 Endpoint: GET /search

Query parameters: `q`, `project`, `repo`, `source`, `limit`, `offset`.
Returns ranked search results with snippets.

### 4.9 Endpoint: POST /ingest/batch

Batch ingest endpoint per ingester-spec section 12:
- Accepts array of events with idempotency keys
- Checks `ingestion_log` for duplicates
- Processes each event (upsert session, append message, etc.)
- Returns per-event status (success/duplicate/error)
- Wraps in transaction per batch

### 4.10 Endpoint: GET /health

Returns 200 with DB connectivity status. No auth required.

---

## Phase 5: Ingestor Service
**Steps: 5.1 – 5.14**

### 5.1 Project Structure

```
OpenBrain.Ingestor/
├── Program.cs                    # Host builder
├── Worker.cs                     # BackgroundService main loop
├── Configuration/
│   ├── IngestorConfig.cs         # Config POCO
│   └── openbrain.ingestor.json   # Default config (template)
├── Adapters/
│   ├── ISourceAdapter.cs         # Interface
│   ├── ClaudeCodeAdapter.cs      # Claude Code JSONL parser
│   └── CodexCliAdapter.cs        # Codex CLI JSONL/log parser
├── Checkpointing/
│   ├── ICheckpointStore.cs       # Interface
│   └── JsonCheckpointStore.cs    # JSON file-based store
├── Redaction/
│   ├── IRedactionPipeline.cs     # Interface
│   ├── RedactionPipeline.cs      # Regex-based redaction
│   └── DefaultRedactionRules.cs  # Built-in patterns
├── Transport/
│   ├── IOpenBrainClient.cs       # API client interface
│   ├── OpenBrainHttpClient.cs    # HTTP client with Polly retry
│   └── OutboxStore.cs            # Local disk outbox for reliability
└── appsettings.json
```

### 5.2 Configuration Model (IngestorConfig.cs)

Maps to `openbrain.ingestor.json`:
- `OpenBrainApiBaseUrl` — default `http://localhost:5000`
- `ApiKey` — overridden by `OPENBRAIN_API_KEY` env var
- `ClientId` — e.g., `"eric-windows-01"`
- `Sources[]` — name, enabled, watchPaths[], filePatterns[], adapterType
- `ScanIntervalSeconds` — default 5
- `BatchSize` — default 100
- `Redaction` — mode (off/basic/aggressive), rulesFile, exclusions[]
- `Outbox` — path, maxSizeMb

### 5.3 ISourceAdapter Interface

```csharp
public interface ISourceAdapter
{
    string SourceName { get; }
    IEnumerable<string> DiscoverFiles(SourceConfig config);
    IEnumerable<RawRecord> ReadNewRecords(string filePath, Checkpoint checkpoint);
    IEnumerable<IngestEvent> ParseRecord(RawRecord raw);
    string IdentifySession(RawRecord raw);
    DateTimeOffset IdentifyTimestamp(RawRecord raw);
}
```

### 5.4 Claude Code Adapter

Parses JSONL files from `~/.claude/projects/*/uuid.jsonl`:

**Record format (observed):**
```json
{
  "type": "user",            // or "assistant", "tool_use", "tool_result", etc.
  "sessionId": "uuid",
  "version": "2.1.63",
  "gitBranch": "main",
  "cwd": "C:\\Users\\...",
  "message": {
    "role": "user",
    "content": "..."        // string or array of content blocks
  },
  "uuid": "message-uuid",
  "parentUuid": "...",
  "timestamp": "ISO-8601"
}
```

Additional types observed:
- `file-history-snapshot` — skip (internal to Claude Code)
- Messages with `isMeta: true` — command expansions (include but tag)

Adapter maps:
- `sessionId` → `sessionExternalId`
- `cwd` path → derive `project` and `repo` from directory name
- `gitBranch` → `branch`
- `message.role` → `role`
- `message.content` → `content` (flatten array content blocks to text)
- `uuid` → `messageExternalId`
- `timestamp` → `createdAt`

### 5.5 Codex CLI Adapter

Parses two file types:

**history.jsonl format:**
```json
{
  "session_id": "uuid",
  "ts": 1767724031,          // Unix epoch seconds
  "text": "user prompt text"
}
```

Maps:
- `session_id` → `sessionExternalId`
- `ts` → `createdAt` (convert epoch to DateTimeOffset)
- `text` → `content`
- Role always `user` (history.jsonl only contains user prompts)

**codex-tui.log format:**
- Structured log lines with timestamps
- `ToolCall:` lines contain tool name + JSON args
- Parse with regex: `^(\S+)\s+INFO\s+ToolCall:\s+(\S+)\s+(.+)$`

### 5.6 Checkpoint Store

JSON file at `%APPDATA%\OpenBrain\Ingestor\checkpoints.json`:

```json
{
  "checkpoints": {
    "/c/Users/erica/.claude/projects/.../uuid.jsonl": {
      "lastProcessedOffset": 142,
      "lastProcessedTimestamp": "2026-03-02T20:59:08Z",
      "fileSize": 48230,
      "fileMtime": "2026-03-02T21:00:00Z",
      "lastSeenAt": "2026-03-02T21:05:00Z"
    }
  }
}
```

Rotation detection:
- If `fileSize` shrinks or `fileMtime` is older → file was rotated → reset offset to 0

### 5.7 Redaction Pipeline

**Basic mode** regex patterns:
| Pattern | Replacement |
|---------|-------------|
| `sk-[a-zA-Z0-9]{20,}` | `[REDACTED:API_KEY]` |
| `anthropic-[a-zA-Z0-9-]{20,}` | `[REDACTED:API_KEY]` |
| `Bearer\s+[a-zA-Z0-9\-._~+/]+=*` | `[REDACTED:BEARER]` |
| `-----BEGIN .* PRIVATE KEY-----[\s\S]*?-----END .* PRIVATE KEY-----` | `[REDACTED:PRIVATE_KEY]` |
| `(password\|passwd\|pwd)\s*[=:]\s*\S+` | `[REDACTED:PASSWORD]` |
| Connection strings with `Password=` | `[REDACTED:CONNSTRING]` |
| AWS key patterns `AKIA[A-Z0-9]{16}` | `[REDACTED:AWS_KEY]` |

**Exclusion rules:**
- Skip files matching `*.pem`, `*.pfx`, `*.key`, `.env*`
- Skip directories containing `secrets`, `private`

### 5.8 OpenBrain HTTP Client

- Base URL from config
- `X-API-Key` header
- Polly retry: 5 attempts, exponential backoff (1s, 2s, 4s, 8s, 16s)
- Circuit breaker: open after 3 consecutive failures, half-open after 30s
- Timeout: 30s per request

### 5.9 Outbox Store

Local disk queue at `%APPDATA%\OpenBrain\Ingestor\outbox\`:
- Each failed batch saved as `{timestamp}-{guid}.json`
- Worker retries outbox on each scan cycle before processing new files
- Max outbox size configurable (default 100MB)
- Oldest files purged if limit exceeded

### 5.10 Worker Main Loop

```
loop every ScanIntervalSeconds:
  1. Retry any outbox items first
  2. For each enabled source adapter:
     a. DiscoverFiles() — find all candidate files
     b. For each file:
        - Load checkpoint
        - ReadNewRecords(file, checkpoint) — incremental read
        - ParseRecord() each raw record → normalized events
        - Apply redaction pipeline
        - Batch events (up to BatchSize)
        - Send batch to OpenBrain API
        - On success: update checkpoint
        - On failure: write to outbox
  3. Log metrics (ingestion rate, backlog, errors)
```

### 5.11 CLI Mode

Support `--once` flag for single-pass ingestion (no service loop):
```
OpenBrain.Ingestor.exe --once
OpenBrain.Ingestor.exe --test-redaction  # Dry-run showing what would be redacted
OpenBrain.Ingestor.exe --status          # Show checkpoint state
```

### 5.12 Windows Service Registration

Can run as:
- Console app (development)
- Windows Service (production via `sc create` or `New-Service`)
- Task Scheduler (periodic)

Use `Microsoft.Extensions.Hosting.WindowsServices` for service support.

### 5.13 Health and Observability

Internal metrics tracked:
- Events ingested (counter)
- Events failed (counter)
- Outbox size (gauge)
- Last successful upload (timestamp)
- Redactions applied (counter)
- Parse errors (counter, with safe truncation)

Optional: expose `/health` on a localhost port for monitoring.

### 5.14 Default Source Configuration

Pre-configured for Eric's machine:

```json
{
  "sources": [
    {
      "name": "claude-code",
      "enabled": true,
      "adapterType": "ClaudeCode",
      "watchPaths": ["C:\\Users\\erica\\.claude\\projects"],
      "filePatterns": ["*.jsonl"]
    },
    {
      "name": "codex-cli",
      "enabled": true,
      "adapterType": "CodexCli",
      "watchPaths": ["C:\\Users\\erica\\.codex"],
      "filePatterns": ["history.jsonl", "codex-tui.log"]
    }
  ]
}
```

---

## Phase 6: MCP Server
**Steps: 6.1 – 6.4**

### 6.1 MCP Server Setup

Thin .NET Minimal API that wraps OpenBrain API calls as MCP tools.
Uses the official `ModelContextProtocol` .NET SDK (or implements the JSON-RPC protocol directly).

### 6.2 MCP Tools

| Tool | Description | Maps to API |
|------|-------------|-------------|
| `create_session` | Create a new session | `POST /sessions` |
| `append_message` | Add a message to session | `POST /sessions/{id}/messages` |
| `append_tool_call` | Record a tool call | `POST /sessions/{id}/toolcalls` |
| `search` | Search message history | `GET /search` |
| `get_session` | Retrieve session + messages | `GET /sessions/{id}` |
| `summarize_session` | Get session summary | Future (v0.2) |

### 6.3 MCP Tool Schema

Each tool returns structured JSON only. Input/output schemas defined per MCP spec.

### 6.4 MCP Registration

Register as local MCP server in Claude Code config:
```json
{
  "mcpServers": {
    "openbrain": {
      "command": "dotnet",
      "args": ["run", "--project", "src/OpenBrain.Mcp"]
    }
  }
}
```

---

## Phase 7: Security & Configuration
**Steps: 7.1 – 7.4**

### 7.1 Secrets Management

- API keys stored in .NET User Secrets (development)
- Environment variables in production
- Never committed to repo
- Connection string: `OPENBRAIN_CONNECTION_STRING` env var
- API auth key: `OPENBRAIN_API_KEY` env var

### 7.2 .gitignore Updates

Add:
```
appsettings.Development.json
*.user
logs/
*.pfx
.env
```

### 7.3 Rate Limiting

Use ASP.NET Core rate limiting middleware:
- Fixed window: 100 requests/minute per API key
- Sliding window for search: 30 requests/minute

### 7.4 Audit Logging

Log all API mutations with:
- Timestamp, endpoint, client ID, API key hash, request size
- Store in NLog file target (separate from app logs)

---

## Phase 8: Testing
**Steps: 8.1 – 8.4**

### 8.1 Unit Tests

- Repository tests with EF Core InMemory provider
- Redaction pipeline tests (verify patterns catch known secret formats)
- Claude Code adapter parsing tests (use sample JSONL from actual files)
- Codex CLI adapter parsing tests
- Checkpoint store tests (rotation detection, safe restart)

### 8.2 Integration Tests

- API endpoint tests with TestServer + real PostgreSQL
- Batch ingest idempotency tests (send same batch twice → no duplicates)
- FTS search tests (insert messages → search → verify ranking)

### 8.3 End-to-End Test

Manual test script:
1. Run db-init.sql
2. Start API
3. Run ingestor `--once`
4. Verify sessions/messages appear in DB
5. Search for known terms
6. Verify no duplicate ingestion on re-run

### 8.4 Redaction Test Mode

`OpenBrain.Ingestor.exe --test-redaction` scans all sources and prints what would be redacted without uploading.

---

## Implementation Order (Recommended)

| Step | Phase | Description | Dependencies |
|------|-------|-------------|--------------|
| 1 | Phase 2.1 | Create solution structure | None |
| 2 | Phase 2.2–2.4 | Define models + DTOs + events | Step 1 |
| 3 | Phase 2.5–2.6 | NuGet packages + NLog config | Step 1 |
| 4 | Phase 1.1–1.7 | Database creation + SQL script | None (parallel) |
| 5 | Phase 3.1–3.5 | Data access layer + migrations | Steps 2, 4 |
| 6 | Phase 4.1–4.10 | API endpoints | Step 5 |
| 7 | Phase 7.1–7.4 | Security + config | Step 6 |
| 8 | Phase 5.1–5.14 | Ingestor service | Steps 2, 6 |
| 9 | Phase 8.1–8.4 | Tests | Steps 6, 8 |
| 10 | Phase 6.1–6.4 | MCP server | Step 6 |

---

## Files to Create/Modify Summary

### New Files (in creation order)
1. `scripts/db-init.sql` — Full database setup script
2. `src/OpenBrain.sln` — Solution file
3. `src/OpenBrain.Models/` — All entity + DTO classes (~12 files)
4. `src/OpenBrain.Data/OpenBrainDbContext.cs` — EF Core context
5. `src/OpenBrain.Data/Repositories/` — 6 repository interfaces + implementations
6. `src/OpenBrain.Api/Program.cs` — API host with all endpoints
7. `src/OpenBrain.Api/Middleware/ApiKeyAuthMiddleware.cs`
8. `src/OpenBrain.Api/nlog.config`
9. `src/OpenBrain.Ingestor/` — All ingestor files (~14 files)
10. `src/OpenBrain.Mcp/Program.cs` — MCP server
11. `tests/` — Test projects

### Modified Files
1. `.claude/project-info.md` — Update with OpenBrain-specific info
2. `.claude/settings.json` — Update project name and description
3. `.gitignore` — Add .NET-specific exclusions

---

## Open Decisions (for Eric to confirm)

1. **Application DB user password** — Should I generate one, or do you have a preferred password for the `openbrain_app` PostgreSQL user?
2. **API authentication key** — Generate a random key for initial development?
3. **MCP SDK** — Use the official `ModelContextProtocol` NuGet package, or implement JSON-RPC protocol directly?
4. **Cloudflare Tunnel** — Defer to Phase 2 deployment, or set up now?
5. **pgvector** — Install the extension now (requires downloading the Windows build), or defer to Phase 2?

---

## Success Criteria (from spec)

- [ ] Prompts never lost
- [ ] Search returns relevant prior solutions
- [ ] MCP agents can store and retrieve memory reliably
- [ ] Works locally (internet-accessible deferred to Phase 2)
- [ ] Minimal operational overhead
- [ ] Ingestor is restart-safe and idempotent
- [ ] Redaction basic mode works with configurable patterns
