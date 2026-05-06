# ai-memory

ai-memory is a cross-platform LLM work ledger and code index. It tracks prompts, sessions, tool calls, and artifacts across AI assistants (Claude, OpenAI, local models), and maintains a structured, queryable index of your codebases. It is designed for power users who want durable memory across agentic workflows without repeating work.

---

## Architecture

```
+--------------------------+       +---------------------------+
|  Claude Code / Codex CLI |       |  Claude Desktop / MCP     |
|  (local, file-based)     |       |  client                   |
+----------+---------------+       +------------+--------------+
           |                                    |
           v                                    v
+----------+---------------+       +------------+--------------+
|  AIMemory.Ingestor         |       |  AIMemory.Mcp              |
|  - Claude Code adapter    |       |  - Session tools          |
|  - Code adapter           | ----> |  - Code tools             |
|  - Checkpoint store       |       +------------+--------------+
|  - Batch + retry queue    |                    |
+----------+---------------+                    |
           |                                    |
           v                                    v
+----------+----------------------------------------------------+
|  AIMemory.Api  (ASP.NET Core minimal API)                      |
|                                                               |
|  /api/sessions    /api/ingest/batch    /api/code/*            |
|  /api/search      /api/keys            /api/stats             |
|                                                               |
|  Auth: cookie (web UI) + API key (ingestor / MCP)             |
|  Rate limiting: fixed window (general), sliding (search)      |
+-----------------+---------------------------------------------+
                  |
                  v
+------------------+------------------+
|  Database (EF Core)                  |
|  SQLite (dev / first run)            |
|  PostgreSQL (production)             |
+--------------------------------------+
                  ^
                  |
+------------------+
|  AIMemory.Client   |
|  React + Vite SPA |
|  (embedded in API)|
+------------------+
```

---

## Projects

| Project | Description |
|---|---|
| `AIMemory.Api` | ASP.NET Core minimal API. Hosts the REST API and serves the React SPA. |
| `AIMemory.Data` | EF Core DbContext, migrations, and repositories. SQLite + PostgreSQL dual provider. |
| `AIMemory.Models` | Shared entity and DTO types. |
| `AIMemory.Ingestor` | Local background agent. Reads Claude Code and code repositories, batches and pushes events to the API. |
| `AIMemory.Ingestor.Installer` | Windows installer / service registration for the ingestor. |
| `AIMemory.Ingestor.ConfigApp` | Desktop configuration utility for the ingestor. |
| `AIMemory.Mcp` | MCP server. Thin wrapper that exposes session and code tools over the Model Context Protocol. |
| `AIMemory.CodeIndex` | Code parsing library. Language parsers (C#, Python, TypeScript, Go) plus file filtering. |
| `aimemory.client` | React + Vite SPA. Also embedded under `AIMemory.Api/clientapp/`. |

---

## Key Features

### Session Tracking
- Create and retrieve named sessions scoped to project, repo, and branch.
- Append messages (user / assistant / system / tool), tool calls, and artifacts.
- Full-text search across sessions by content, project, or source.
- Cost and token telemetry per message.

### Code Indexing
- Index local folders or repositories by pointing the ingestor at them.
- Parses symbols (functions, classes, methods, interfaces, structs, properties, enums) from C#, Python, TypeScript, and Go source files.
- Retrieves source by byte offset — surgical reads rather than full-file loads.
- Queryable via REST or MCP tools.

### MCP Tools
- Session tools: `create_session`, `append_message`, `append_tool_call`, `search`, `get_session`.
- Code tools: `IndexFolder`, `ListCodeRepos`, `GetFileTree`, `GetFileOutline`, `GetRepoOutline`, `GetSymbol`, `GetSymbols`, `SearchSymbols`, `SearchCodeText`, `RemoveCodeRepo`.

### API Key Management
- Scoped keys: `ingest`, `code`, `mcp`, `admin`.
- Keys are stored as SHA-256 hashes; only the prefix is retained for display.
- 60-second in-memory cache for auth performance.
- Optional expiry and last-used tracking.

### Web UI Dashboard
- Session browser with message history, tool calls, and artifacts.
- Code repository browser with file tree and symbol outline views.
- API key management.
- Activity stats and daily charts.

### First-Run Setup Wizard
- No database required until wizard completes.
- Creates admin user and writes configuration to `%ProgramData%\AIMemory\Api\`.
- Supports SQLite (default) or PostgreSQL.

---

## Getting Started

### Prerequisites

- .NET 10 SDK
- Node.js 20+ and npm
- (Optional) PostgreSQL if not using the SQLite default

### Build

```bash
cd src
dotnet build AIMemory.slnx
cd aimemory.client
npm install
```

### Run (development)

Open two terminals:

```bash
# Terminal 1 — API
cd src/AIMemory.Api
dotnet run

# Terminal 2 — React dev server
cd src/aimemory.client
npm run dev
```

The API listens on `http://localhost:5219`. The Vite dev server listens on `http://localhost:5173` and proxies `/api/*` requests to the API. Open `http://localhost:5173` in a browser.

On first run, the setup wizard will prompt for credentials and database selection.

### Run (production)

```bash
cd src/AIMemory.Api
dotnet publish -c Release -o ./publish
# Copy aimemory.client/dist to publish/wwwroot
cd publish
dotnet AIMemory.Api.dll
```

---

## Configuration

### API (`AIMemory.Api`)

Configuration is written to `%ProgramData%\AIMemory\Api\appsettings.json` by the setup wizard or manually.

```json
{
  "AIMemory": {
    "DatabaseProvider": "SQLite",
    "Port": 5219
  },
  "ConnectionStrings": {
    "AIMemory": "Data Source=C:\\path\\to\\aimemory.db"
  }
}
```

Environment variable override: `AIMEMORY_API_KEY` — acts as a legacy admin-scoped key (bypasses DB key lookup).

### Ingestor (`AIMemory.Ingestor`)

Config file: `aimemory.ingestor.json`

```json
{
  "AIMemoryApiBaseUrl": "http://localhost:5219",
  "ApiKey": "aimemory_...",
  "ClientId": "my-machine-01",
  "Sources": [
    {
      "Name": "claude-code",
      "Enabled": true,
      "AdapterType": "ClaudeCode",
      "WatchPaths": [ "C:\\Users\\you\\.claude\\projects" ]
    },
    {
      "Name": "code-index",
      "Enabled": true,
      "AdapterType": "Code",
      "WatchPaths": [ "C:\\source\\repos\\MyProject" ]
    }
  ],
  "ScanIntervalSeconds": 30,
  "BatchSize": 100
}
```

Environment variable overrides:
- `AIMEMORY_API_URL`
- `AIMEMORY_API_KEY`
- `AIMEMORY_CLIENT_ID`

### MCP (`AIMemory.Mcp`)

The MCP server connects to the API using an API key with `mcp` or `admin` scope. Configure via environment variables or `appsettings.json` in the MCP project.

---

## API Endpoints

### Setup and Auth

| Method | Path | Description |
|---|---|---|
| GET | `/api/setup/status` | Check whether setup wizard is needed |
| POST | `/api/setup/init` | Complete first-run setup |
| POST | `/api/auth/login` | Log in (sets cookie) |
| POST | `/api/auth/logout` | Log out |
| GET | `/api/auth/me` | Current user info |

### Sessions

| Method | Path | Description |
|---|---|---|
| POST | `/api/sessions` | Create a session |
| GET | `/api/sessions` | List sessions (filterable by project, repo, source, tag, date range) |
| GET | `/api/sessions/{id}` | Get session with messages, tool calls, and artifacts |
| POST | `/api/sessions/{id}/messages` | Append a message |
| POST | `/api/sessions/{id}/toolcalls` | Append a tool call |
| POST | `/api/sessions/{id}/artifacts` | Append an artifact |

### Search

| Method | Path | Description |
|---|---|---|
| GET | `/api/search?q=...` | Full-text search across sessions |

### Ingestion

| Method | Path | Description |
|---|---|---|
| POST | `/api/ingest/batch` | Batch ingest events (SessionUpsert, MessageAppend, ToolCallAppend, ArtifactAppend, CodeFileUpsert, CodeSymbolBatch) |
| GET | `/api/ingestion-log` | View ingestion history |

### Code Index

| Method | Path | Description |
|---|---|---|
| POST | `/api/code/index-folder` | Index a local folder |
| GET | `/api/code/repos` | List all indexed repositories |
| GET | `/api/code/repos/{id}` | Get repository details |
| DELETE | `/api/code/repos/{id}` | Remove a repository |
| POST | `/api/code/repos/{id}/reindex` | Re-index a repository |
| GET | `/api/code/repos/{id}/tree` | Get file tree |
| GET | `/api/code/repos/{id}/outline` | Get repository or file symbol outline |
| GET | `/api/code/repos/{id}/symbol?key=...` | Get a single symbol with source |
| POST | `/api/code/repos/{id}/symbols` | Batch-get symbols |
| GET | `/api/code/search/symbols?q=...` | Search symbols by name or qualified name |
| GET | `/api/code/search/text?q=...` | Full-text search within indexed files |

### API Key Management

| Method | Path | Description |
|---|---|---|
| GET | `/api/keys` | List all API keys |
| POST | `/api/keys` | Create a new API key |
| GET | `/api/keys/{id}` | Get key details |
| PATCH | `/api/keys/{id}` | Update name, scopes, or active status |
| DELETE | `/api/keys/{id}` | Delete a key |

### Observability

| Method | Path | Description |
|---|---|---|
| GET | `/api/health` | Database connectivity check |
| GET | `/api/stats` | Session and message totals, daily activity (30 days) |

---

## MCP Tools

### Session Tools

| Tool | Description |
|---|---|
| `create_session` | Create a named session |
| `append_message` | Append a message to a session |
| `append_tool_call` | Append a tool call record to a session |
| `search` | Full-text search across session history |
| `get_session` | Retrieve a session with its message history |

### Code Tools

| Tool | Description |
|---|---|
| `IndexFolder` | Index a local code folder; returns repo info with file and symbol counts |
| `ListCodeRepos` | List all indexed repositories |
| `GetFileTree` | Get all files in a repository with language and size |
| `GetFileOutline` | Get all symbols in a specific file |
| `GetRepoOutline` | Get a high-level overview of a repository |
| `GetSymbol` | Get the full source of a symbol by key (`filepath::QualifiedName#kind`) |
| `GetSymbols` | Batch-retrieve multiple symbols by key array |
| `SearchSymbols` | Search symbols by name with optional kind filter |
| `SearchCodeText` | Full-text search within indexed source files |
| `RemoveCodeRepo` | Delete a repository from the index |

---

## Tech Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core (.NET 10), minimal API |
| ORM | Entity Framework Core 9 |
| Database | SQLite (default), PostgreSQL |
| Web UI | React 19, Vite 6, TypeScript |
| MCP | ModelContextProtocol .NET SDK |
| Logging | NLog |
| Auth | Cookie auth (web UI), API key (X-API-Key header) |
| Tests | xUnit |

---

## API Key Scopes

| Scope | Access |
|---|---|
| `ingest` | POST to `/api/ingest/batch` |
| `code` | All `/api/code/*` and `/api/ingest/batch` |
| `mcp` | Sessions, search, code read |
| `admin` | All endpoints |
