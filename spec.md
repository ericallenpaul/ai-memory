# AIMemory Specification
Version: 0.3
Status: Active
Author: Eric Paul
Date: 2026-03-10

---

# 1. Overview

AIMemory is a personal LLM work ledger and memory system designed to track prompts, sessions, tool calls, artifacts, and decisions across AI providers (Claude, Codex, OpenAI, local models, etc.). It also maintains a structured, queryable index of local and remote code repositories so agents can read source code efficiently without loading entire files.

It provides:

- Durable storage of AI interactions
- Structured retrieval (full-text search + embeddings)
- MCP access for agentic workflows
- Internet-accessible API
- Cost and token telemetry
- Cross-provider session continuity
- Code index with symbol-level retrieval by byte offset
- API key management with scoped permissions
- Web UI dashboard for browsing sessions and code

Primary goal:

Prevent repeating work. Preserve context. Enable agentic continuity.

---

# 2. Design Principles

1. Store everything as events.
2. Keep raw data immutable.
3. Add derived data (summaries, embeddings) separately.
4. Be local-first but internet-accessible.
5. Secure by default.
6. Minimal surface area for MCP.
7. Structured outputs only.

---

# 3. High-Level Architecture

Components:

- AIMemory API (.NET 10 minimal API)
- React + Vite web UI (embedded SPA)
- EF Core + SQLite (default) or PostgreSQL
- Ingestor (local background agent)
- MCP Server (thin wrapper over API)
- CodeIndex library (language parsers, file filter)
- Search layer (Postgres FTS; SQLite fallback)
- Optional embedding layer (pgvector, future)
- Secure tunnel (Cloudflare Tunnel recommended)

Logical Flow:

```
Agent or LLM
  -> MCP tools
  -> AIMemory API
  -> Database (SQLite or PostgreSQL)
  -> Search or retrieval
  -> Returned context

Ingestor (local)
  -> Reads Claude Code transcripts, local code folders
  -> Normalizes to events
  -> POST /api/ingest/batch -> AIMemory API
```

---

# 4. Core Concepts

## 4.1 Session

A logical container for a conversation or workflow.

Examples:
- "Jenkins Pipeline Refactor"
- "AI Gateway Design"
- "VehicleLead RabbitMQ Implementation"

Sessions are project-scoped and time-bound. They carry optional `source`, `machineName`, and `externalId` for ingestor-driven upserts.

## 4.2 Message

A single interaction unit.

Roles:
- system
- user
- assistant
- tool

Messages are immutable once written. They carry optional token counts, cost in USD, latency, provider, model, and external ID.

## 4.3 Tool Call

Structured record of an agent invoking a tool.

Includes:
- tool name
- arguments (JSON)
- results (JSON)
- timestamps

## 4.4 Artifact

External or derived output associated with a session.

Examples:
- generated file
- SQL script
- design doc
- API spec
- commit hash
- diff summary

Types: file, snippet, link, diff, commit, other.

## 4.5 Code Repository

A named, indexed view of a local folder or remote repository. Tracks files, symbols, file count, and symbol count.

## 4.6 Code File

A single source file within an indexed repository. Tracks language, file size, content hash, and index timestamp.

## 4.7 Code Symbol

A named declaration within a code file: function, class, method, interface, struct, property, or enum. Tracks qualified name, kind, signature, start/end line, start/end byte, and optional parent symbol. The byte offsets enable surgical content extraction without reading the whole file.

## 4.8 API Key

A scoped credential for programmatic access. Stored as SHA-256 hash; only the first 8 hex characters are retained for display. Scopes: ingest, code, mcp, admin.

---

# 5. Data Model

## 5.1 Table: sessions

| Column | Type | Notes |
|---|---|---|
| SessionId | UUID PK | |
| ExternalId | TEXT | Source-assigned stable ID for upserts |
| Title | TEXT | |
| Project | TEXT | |
| Repo | TEXT | |
| Branch | TEXT | |
| Tags | TEXT[] | |
| Source | TEXT | e.g., "claude-code", "codex-cli" |
| MachineName | TEXT | Origin machine for ingestor-driven records |
| IsArchived | BOOLEAN | Default false |
| CreatedAt | TIMESTAMP | |
| UpdatedAt | TIMESTAMP | |

Indexes: Project, Repo, Source, CreatedAt.

## 5.2 Table: messages

| Column | Type | Notes |
|---|---|---|
| MessageId | UUID PK | |
| SessionId | UUID FK | |
| ExternalId | TEXT | |
| Role | TEXT | system, user, assistant, tool |
| Content | TEXT | |
| Provider | TEXT | |
| Model | TEXT | |
| RequestId | TEXT | |
| TokenIn | INT | |
| TokenOut | INT | |
| CostUsd | NUMERIC(10,6) | |
| LatencyMs | INT | |
| CreatedAt | TIMESTAMP | |

Optional (future): ContentVector VECTOR, ContentTSVector TSVECTOR.

## 5.3 Table: tool_calls

| Column | Type | Notes |
|---|---|---|
| ToolCallId | UUID PK | |
| SessionId | UUID FK | |
| ExternalId | TEXT | |
| ToolName | TEXT | |
| ArgumentsJson | JSONB | |
| ResultJson | JSONB | |
| CreatedAt | TIMESTAMP | |

## 5.4 Table: artifacts

| Column | Type | Notes |
|---|---|---|
| ArtifactId | UUID PK | |
| SessionId | UUID FK | |
| ExternalId | TEXT | |
| Type | TEXT | file, snippet, link, diff, commit, other |
| PathOrUrl | TEXT | |
| Hash | TEXT | |
| MetadataJson | JSONB | |
| CreatedAt | TIMESTAMP | |

## 5.5 Table: code_repositories

| Column | Type | Notes |
|---|---|---|
| RepositoryId | UUID PK | |
| Name | TEXT | Unique name, e.g., folder basename |
| SourceType | TEXT | "local" or "github" |
| SourcePath | TEXT | Absolute path on the indexing machine |
| DefaultBranch | TEXT | |
| FileCount | INT | Maintained by stats update |
| SymbolCount | INT | Maintained by stats update |
| IndexedAt | TIMESTAMP | |
| UpdatedAt | TIMESTAMP | |

## 5.6 Table: code_files

| Column | Type | Notes |
|---|---|---|
| FileId | UUID PK | |
| RepositoryId | UUID FK | |
| FilePath | TEXT | Relative to repository root, forward slashes |
| Language | TEXT | cs, py, ts, go, unknown, etc. |
| FileSize | BIGINT | |
| ContentHash | TEXT | SHA-256 hex |
| IndexedAt | TIMESTAMP | |

## 5.7 Table: code_symbols

| Column | Type | Notes |
|---|---|---|
| SymbolId | UUID PK | |
| FileId | UUID FK | |
| RepositoryId | UUID FK | |
| SymbolKey | TEXT | `filepath::QualifiedName#kind` — globally unique within repo |
| Name | TEXT | Short name |
| QualifiedName | TEXT | Fully qualified (e.g., `MyNamespace.MyClass.MyMethod`) |
| Kind | TEXT | function, class, method, interface, struct, property, enum |
| Signature | TEXT | Full declaration line |
| StartLine | INT | |
| EndLine | INT | |
| StartByte | BIGINT | Byte offset in file |
| EndByte | BIGINT | Byte offset in file |
| ParentSymbolKey | TEXT | Nullable, for nested declarations |
| IndexedAt | TIMESTAMP | |

## 5.8 Table: api_keys

| Column | Type | Notes |
|---|---|---|
| ApiKeyId | UUID PK | |
| Name | TEXT | Human label |
| KeyHash | TEXT | SHA-256 of raw key |
| KeyPrefix | TEXT | First 8 hex chars after `aimemory_` prefix |
| Scopes | TEXT[] | ingest, code, mcp, admin |
| IsActive | BOOLEAN | |
| CreatedAt | TIMESTAMP | |
| LastUsedAt | TIMESTAMP | Updated async on each use |
| ExpiresAt | TIMESTAMP | Nullable |
| CreatedBy | TEXT | Username from cookie session |

## 5.9 Table: ingestion_log

Tracks every successfully ingested event for deduplication.

Key fields: IdempotencyKey, EventType, Source, SourcePath, RecordOffset, MachineName, IngestedAt.

## 5.10 Table: admin_users

Single-user credentials table. Username and BCrypt password hash.

---

# 6. API Surface (HTTP)

Base URL: `http://localhost:5219` (development), configurable in production.

Authentication:
- Cookie auth for web UI (POST `/api/auth/login`)
- API key via `X-API-Key` header for programmatic access
- Legacy environment variable key (`AIMEMORY_API_KEY`) acts as admin scope

Rate limiting:
- General endpoints: 100 requests per minute (fixed window)
- Search endpoints: 30 requests per minute (sliding window)

---

## 6.1 Setup and Auth

```
GET  /api/setup/status       -> { needsSetup: bool }
POST /api/setup/init         -> create admin user and persist config

POST /api/auth/login         -> set auth cookie
POST /api/auth/logout        -> clear auth cookie
GET  /api/auth/me            -> { username, id }
```

## 6.2 Sessions

```
POST /api/sessions
  Body: { title, project, repo, branch, tags, source, externalId }
  Returns: { sessionId }

GET /api/sessions
  Query: project, repo, source, tag, from, to, limit, offset

GET /api/sessions/{sessionId}
  Query: limit, includeToolCalls, includeArtifacts

POST /api/sessions/{sessionId}/messages
  Body: { role, content, provider, model, tokenIn, tokenOut, costUsd, latencyMs, externalId }

POST /api/sessions/{sessionId}/toolcalls
  Body: { toolName, argumentsJson, resultJson, externalId }

POST /api/sessions/{sessionId}/artifacts
  Body: { type, pathOrUrl, hash, metadataJson, externalId }
```

## 6.3 Search

```
GET /api/search?q=...&project=...&repo=...&source=...&limit=10&offset=0

Response: [{ sessionId, snippet, createdAt, ... }]
```

## 6.4 Batch Ingestion

```
POST /api/ingest/batch
  Body: {
    clientId,
    source,
    machineName,
    events: [{ type, idempotencyKey, payload }]
  }

  Supported event types:
    SessionUpsert
    MessageAppend
    ToolCallAppend
    ArtifactAppend
    CodeFileUpsert
    CodeSymbolBatch

  Response: { total, succeeded, duplicates, failed, results: [{ idempotencyKey, status, error }] }

GET /api/ingestion-log
  Query: source, eventType, limit, offset
```

## 6.5 Code Index

```
POST /api/code/index-folder
  Body: { folderPath, name? }
  Returns: CodeRepoResponse

GET  /api/code/repos
GET  /api/code/repos/{repoId}
DELETE /api/code/repos/{repoId}
POST /api/code/repos/{repoId}/reindex

GET  /api/code/repos/{repoId}/tree
GET  /api/code/repos/{repoId}/outline
  Query: file (optional — if omitted, returns repo-level outline)
GET  /api/code/repos/{repoId}/symbol?key=filepath::QualifiedName#kind
POST /api/code/repos/{repoId}/symbols
  Body: ["key1", "key2"]

GET  /api/code/search/symbols?q=...&repo=...&kind=...&limit=20
GET  /api/code/search/text?q=...&repo=...&file=...&limit=20
```

## 6.6 API Key Management

```
GET    /api/keys
POST   /api/keys
  Body: { name, scopes: ["ingest"|"code"|"mcp"|"admin"], expiresAt? }
  Returns: { apiKeyId, name, rawKey, keyPrefix, scopes }
  Note: rawKey is returned ONLY on creation and never stored.

GET    /api/keys/{id}
PATCH  /api/keys/{id}
  Body: { name?, scopes?, isActive? }
DELETE /api/keys/{id}
```

## 6.7 Observability

```
GET /api/health
GET /api/stats
  Returns: { totalSessions, totalMessages, totalTokensIn, totalTokensOut,
             totalCostUsd, sessionsToday, dailyStats[30 days] }
```

---

# 7. MCP Tool Surface

Tools exposed via the MCP server. All return structured JSON.

## 7.1 Session Tools

| Tool | Description |
|---|---|
| `create_session` | Create a named session |
| `append_message` | Append a message to a session |
| `append_tool_call` | Append a tool call record |
| `search` | Full-text search across session history |
| `get_session` | Retrieve a session with message history |

## 7.2 Code Tools

| Tool | Description |
|---|---|
| `IndexFolder` | Index a local folder; returns repo info |
| `ListCodeRepos` | List all indexed repositories |
| `GetFileTree` | Files in a repo with language and size |
| `GetFileOutline` | Symbols in a specific file |
| `GetRepoOutline` | High-level overview of a repository |
| `GetSymbol` | Source of a symbol by key (`filepath::QualifiedName#kind`) |
| `GetSymbols` | Batch-retrieve multiple symbols |
| `SearchSymbols` | Search by symbol name or qualified name, with kind filter |
| `SearchCodeText` | Full-text search within indexed source files |
| `RemoveCodeRepo` | Delete a repository from the index |

Symbol keys use the format: `src/Foo.cs::MyNamespace.MyClass.MyMethod#method`

---

# 8. Code Index

The code index allows agents to navigate and read codebases without loading entire files.

## 8.1 Indexing Flow

1. `IndexFolder` (API or ingestor) enumerates supported source files.
2. Each file is parsed by the appropriate language parser.
3. Symbols are stored with start/end byte offsets.
4. Stats (file count, symbol count) are updated on the repository record.

## 8.2 Language Parsers

Supported languages: C#, Python, TypeScript, Go.

Each parser extracts: name, qualified name, kind, signature, start line, end line, start byte, end byte, and parent symbol name.

## 8.3 Symbol Retrieval

When `GetSymbol` is called, the API reads the raw file and slices the bytes from `StartByte` to `EndByte`. This returns only the relevant declaration without reading the entire file into context.

## 8.4 File Filter

The `FileFilter` class applies a configurable include/exclude list by extension and path pattern. Binary files are detected and skipped. Common folders (`node_modules`, `.git`, `bin`, `obj`) are excluded by default.

---

# 9. Security

- HTTPS required for internet-accessible deployments
- API key required for all programmatic access
- Cookie auth for the web UI; HTTP-only, SameSite=Strict
- API keys stored as SHA-256 hashes only
- Rate limiting on all endpoints
- Audit logging via ingestion log
- Secrets stored outside repo (environment variables or ProgramData config)
- Internet exposure via Cloudflare Tunnel recommended

## 9.1 Scope Requirements

| Path prefix | Required scopes |
|---|---|
| `/api/ingest` | ingest, code, or admin |
| `/api/code` | code, mcp, or admin |
| `/api/sessions` | mcp or admin |
| `/api/search` | mcp or admin |
| `/api/keys` | admin |
| Other `/api/*` | any valid key |

---

# 10. Search Strategy

Phase 1 (current):
- PostgreSQL full-text search
- SQLite LIKE-based fallback
- Weighted ranking
- Snippet extraction

Phase 2 (future):
- pgvector embeddings
- Cosine similarity search
- Hybrid search (FTS + vector)

---

# 11. Deployment Strategy

Phase 1:
- Run on Windows workstation or Mac mini
- SQLite for simplicity
- dotnet run or Windows service via installer

Phase 2:
- PostgreSQL for production
- Docker compose
- Reverse proxy or Cloudflare Tunnel
- Automated backups

---

# 12. Non-Goals (v0.3)

- No multi-user RBAC (single admin user)
- No automatic IDE plugin
- No AI summarization pipeline
- No vector DB external dependency (pgvector is optional future)
- No real-time LLM gateway proxying

---

# 13. Future Enhancements

- Multi-user support
- Project-scoped memory injection
- Automatic summarization of long sessions
- Diff tracking against Git
- Cost analytics dashboard
- Agent orchestration layer
- Knowledge graph layer
- Time-based recall weighting
- Secret redaction engine
- GitHub repository indexing (remote source type)
- pgvector semantic search

---

# 14. Success Criteria

- Prompts never lost.
- Search returns relevant prior solutions.
- MCP agents can store and retrieve memory reliably.
- Agents can retrieve specific symbols from large codebases without full-file context loads.
- Works locally and over internet.
- Minimal operational overhead.

---

# 15. Guiding Philosophy

AIMemory is not a chatbot.

It is an append-only, queryable memory substrate for agentic engineering.

It should feel like Git for AI work.
