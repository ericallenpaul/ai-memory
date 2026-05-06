# Plan: Setup Wizard, Web UI, SQLite/Postgres Choice, Embedded Host

**Date**: 2026-03-03
**Branch**: main
**Status**: DRAFT — Awaiting Approval

---

## 1. Problem Statement

The current AIMemory system requires:
- Manual PostgreSQL installation and schema setup (`psql -f db-init.sql`)
- Manual configuration of `appsettings.json` files across multiple projects
- No web-based UI for browsing ingested sessions, messages, and tool calls
- No way for a non-developer to get the system running without significant effort
- Hard PostgreSQL dependency (no lightweight SQLite option)
- No vector database support for semantic search

**Goal**: A single executable that launches, detects first-run, opens a browser-based setup wizard, and after configuration provides a full web UI for browsing data — all self-contained with zero external dependencies beyond .NET runtime.

---

## 2. High-Level Architecture

### New Project: `OpenBrain.Host`

A **single unified executable** that embeds:
- The REST API (existing endpoints, moved from `OpenBrain.Api`)
- A Blazor Server web UI (new)
- A first-run setup wizard (web-based, served by the same process)
- An embedded Kestrel web server (ASP.NET Core built-in — no IIS/nginx needed)
- The ingestor worker (optionally runs in-process as a hosted service)

```
User launches OpenBrain.Host.exe
    ↓
First run? → Browser opens setup wizard (http://localhost:{port}/setup)
    ↓
Setup wizard: DB choice → Port → Web credentials → API keys → Vector DB
    ↓
Config saved to %ProgramData%\OpenBrain\config.json
    ↓
DB initialized (SQLite file created OR PostgreSQL schema applied)
    ↓
Redirect to Web UI (http://localhost:{port})
    ↓
Web UI: Dashboard, Sessions, Search, Logs, Metrics, Settings
```

### Database Abstraction

```
OpenBrain.Data (modified)
├── OpenBrainDbContext.cs          — EF Core context (shared)
├── Providers/
│   ├── PostgresDbContextFactory.cs — Npgsql + pgvector config
│   └── SqliteDbContextFactory.cs   — SQLite + FTS5 config
├── Migrations/
│   ├── Postgres/                   — PostgreSQL-specific migrations
│   └── Sqlite/                     — SQLite-specific migrations
└── Repositories/                   — Unchanged (EF Core abstracts provider)
```

### Full-Text Search Strategy

| Feature | PostgreSQL | SQLite |
|---------|-----------|--------|
| FTS | Built-in tsvector + ts_rank | FTS5 virtual table |
| Vector | pgvector extension | sqlite-vec extension |
| Triggers | Native DB triggers | EF Core SaveChanges hook |

---

## 3. Detailed Implementation Plan

---

### Phase A: Configuration & Bootstrap Infrastructure

#### Step A.1: Create unified configuration model

**New file**: `src/OpenBrain.Models/Configuration/OpenBrainConfig.cs`

```csharp
public class OpenBrainConfig
{
    public string DatabaseProvider { get; set; } = "sqlite"; // "sqlite" or "postgres"
    public string? PostgresConnectionString { get; set; }
    public string SqliteDbPath { get; set; } = ""; // auto-set to %ProgramData%\OpenBrain\openbrain.db
    public int Port { get; set; } = 5080;
    public string ApiKey { get; set; } = ""; // generated during setup
    public string IngestorApiKey { get; set; } = ""; // generated during setup (can be same or separate)
    public string McpApiKey { get; set; } = ""; // generated during setup (can be same or separate)
    public string WebUsername { get; set; } = "";
    public string WebPasswordHash { get; set; } = ""; // bcrypt hash
    public bool VectorSearchEnabled { get; set; } = true;
    public bool IngestorEnabled { get; set; } = true;
    public bool SetupCompleted { get; set; } = false;
    public DateTimeOffset? SetupCompletedAt { get; set; }

    // Ingestor settings (pulled from IngestorConfig)
    public string ClientId { get; set; } = "";
    public int ScanIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
    public List<SourceConfig> Sources { get; set; } = [];
    public RedactionConfig Redaction { get; set; } = new();
}
```

**New file**: `src/OpenBrain.Models/Configuration/ConfigStore.cs`

```csharp
public static class ConfigStore
{
    // Config file location: %ProgramData%\OpenBrain\config.json (Windows)
    //                    or ~/.config/openbrain/config.json (Linux/Mac)
    public static string GetConfigDirectory();
    public static string GetConfigPath();
    public static OpenBrainConfig Load();
    public static void Save(OpenBrainConfig config);
    public static bool IsFirstRun(); // !File.Exists(configPath) || !config.SetupCompleted
    public static string GenerateApiKey(); // "ob-" + 32-char random hex
    public static string HashPassword(string password); // BCrypt
    public static bool VerifyPassword(string password, string hash); // BCrypt
}
```

#### Step A.2: Update IngestorConfig to reference OpenBrainConfig

**File**: `src/OpenBrain.Ingestor/Configuration/IngestorConfig.cs`

No structural changes needed — the `OpenBrainConfig` will contain ingestor settings and map them to `IngestorConfig` at startup. The existing `IngestorConfig` class remains for backward compatibility.

---

### Phase B: Database Provider Abstraction

#### Step B.1: Add SQLite EF Core provider

**File**: `src/OpenBrain.Data/OpenBrain.Data.csproj` — add packages:
- `Microsoft.EntityFrameworkCore.Sqlite` (v10.0.0)
- `BCrypt.Net-Next` (v4.0.3) — for password hashing in ConfigStore

**File**: `src/OpenBrain.Models/OpenBrain.Models.csproj` — add package:
- `BCrypt.Net-Next` (v4.0.3)

#### Step B.2: Modify OpenBrainDbContext for multi-provider support

**File**: `src/OpenBrain.Data/OpenBrainDbContext.cs`

Current state: Hardcoded PostgreSQL-specific column mappings (snake_case via Npgsql conventions).

Changes needed:
- Remove PostgreSQL-specific `HasColumnType("tsvector")` from Message.ContentTsvector
- Add a `DatabaseProvider` property to the context
- In `OnModelCreating`, branch on provider for:
  - Column naming (snake_case for Postgres, PascalCase for SQLite)
  - Index types (GIN for Postgres arrays, standard for SQLite)
  - FTS configuration (tsvector for Postgres, FTS5 shadow table for SQLite)
  - The `ContentTsvector` property is ignored for SQLite (FTS5 handles search separately)

#### Step B.3: Create SQLite FTS5 shadow table support

**New file**: `src/OpenBrain.Data/Search/SqliteFtsManager.cs`

Responsible for:
- Creating `messages_fts` virtual table: `CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(content, session_id UNINDEXED, message_id UNINDEXED)`
- Populating FTS index on message insert (called from repository)
- Querying FTS: `SELECT * FROM messages_fts WHERE messages_fts MATCH ?`
- Ranking: `bm25(messages_fts)` for relevance scoring
- Snippet extraction: `snippet(messages_fts, 0, '<mark>', '</mark>', '...', 32)`

#### Step B.4: Create vector search infrastructure

**New file**: `src/OpenBrain.Data/Search/VectorSearchManager.cs`

For PostgreSQL (pgvector):
- Create extension: `CREATE EXTENSION IF NOT EXISTS vector`
- Add `embedding` column to messages: `ALTER TABLE messages ADD COLUMN embedding vector(1536)`
- Index: `CREATE INDEX ON messages USING ivfflat (embedding vector_cosine_ops)`

For SQLite (sqlite-vec):
- Create virtual table: `CREATE VIRTUAL TABLE IF NOT EXISTS message_vectors USING vec0(message_id TEXT PRIMARY KEY, embedding float[1536])`
- Query: `SELECT * FROM message_vectors WHERE embedding MATCH ? ORDER BY distance LIMIT ?`

> **Note**: Actual embedding generation is a future phase. This step just creates the schema. The vector columns will be nullable and only populated when an embedding provider is configured.

#### Step B.5: Update SearchRepository for dual-provider

**File**: `src/OpenBrain.Data/Repositories/SearchRepository.cs`

Current state: Raw SQL using PostgreSQL `ts_rank`, `ts_headline`, `plainto_tsquery`.

Changes:
- Add constructor parameter for `DatabaseProvider` (string)
- Branch search logic:
  - **PostgreSQL**: Keep existing FTS implementation
  - **SQLite**: Use FTS5 `MATCH` with `bm25()` and `snippet()` via raw SQL

#### Step B.6: Update IngestionRepository for SQLite compatibility

**File**: `src/OpenBrain.Data/Repositories/IngestionRepository.cs`

The `FilterExistingKeysAsync` and `LogBatchAsync` methods use `ExecuteSqlRawAsync` with PostgreSQL-specific `unnest` and `ON CONFLICT`. Need SQLite equivalents:
- `ON CONFLICT` → SQLite supports `INSERT OR IGNORE`
- `unnest` → use parameterized `WHERE key IN (...)` queries

#### Step B.7: Database initialization service

**New file**: `src/OpenBrain.Data/DatabaseInitializer.cs`

```csharp
public class DatabaseInitializer
{
    // For PostgreSQL: Run EF migrations + extensions + FTS trigger + vector extension
    // For SQLite: Run EF migrations + create FTS5 table + create vec0 table
    public async Task InitializeAsync(OpenBrainConfig config);
    public async Task<bool> TestConnectionAsync(OpenBrainConfig config);
}
```

This replaces the manual `db-init.sql` script. The initializer:
1. Creates the database (SQLite: create file; PostgreSQL: `EnsureCreated` or migration)
2. Applies EF Core migrations
3. Creates FTS infrastructure (trigger for Postgres, FTS5 for SQLite)
4. Creates vector tables if `VectorSearchEnabled`
5. Creates the application database user (PostgreSQL only)

---

### Phase C: OpenBrain.Host — Unified Executable

#### Step C.1: Create the Host project

**New directory**: `src/OpenBrain.Host/`

**File**: `src/OpenBrain.Host/OpenBrain.Host.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
    <PackageReference Include="BCrypt.Net-Next" Version="4.0.3" />
    <PackageReference Include="NLog.Web.AspNetCore" Version="6.1.2" />
    <PackageReference Include="Polly" Version="8.6.5" />
    <PackageReference Include="Polly.Extensions.Http" Version="3.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenBrain.Data\OpenBrain.Data.csproj" />
    <ProjectReference Include="..\OpenBrain.Models\OpenBrain.Models.csproj" />
    <ProjectReference Include="..\OpenBrain.Ingestor\OpenBrain.Ingestor.csproj" />
  </ItemGroup>
</Project>
```

#### Step C.2: Program.cs — Unified entry point

**File**: `src/OpenBrain.Host/Program.cs`

```
1. Load OpenBrainConfig from ConfigStore
2. If first run → configure minimal Kestrel on port 5080, serve only /setup routes
3. If configured:
   a. Configure Kestrel on config.Port
   b. Register EF Core with correct provider (SQLite or PostgreSQL)
   c. Register all repositories
   d. Register authentication (cookie auth for web UI, API key for API)
   e. Register Blazor Server services
   f. Register Ingestor hosted service (if IngestorEnabled)
   g. Map API endpoints under /api/*
   h. Map Blazor endpoints under /*
   i. Map /health (no auth)
4. Open browser to http://localhost:{port} (or /setup if first run)
5. Run host
```

#### Step C.3: API endpoints (migrated from OpenBrain.Api)

**New file**: `src/OpenBrain.Host/Api/ApiEndpoints.cs`

Move all endpoint mappings from `OpenBrain.Api/Program.cs` into a static class:
```csharp
public static class ApiEndpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("ApiKey");

        api.MapPost("/sessions", ...);
        api.MapGet("/sessions/{sessionId}", ...);
        api.MapPost("/sessions/{sessionId}/messages", ...);
        api.MapPost("/sessions/{sessionId}/toolcalls", ...);
        api.MapPost("/sessions/{sessionId}/artifacts", ...);
        api.MapGet("/search", ...);
        api.MapPost("/ingest/batch", ...);
    }
}
```

**Key change**: Endpoints move from `/sessions` to `/api/sessions` etc., so the root `/` is free for the web UI. The existing `/sessions` paths remain as aliases for backward compatibility.

#### Step C.4: Authentication — Dual scheme

**New file**: `src/OpenBrain.Host/Auth/AuthConfiguration.cs`

Two authentication schemes:
1. **API Key** (for MCP, Ingestor, external API clients): `X-API-Key` header
2. **Cookie** (for web UI): Login form with username/password → cookie

```csharp
// Policy: "ApiKey" — requires valid X-API-Key header
// Policy: "WebUser" — requires authenticated cookie
// Policy: "ApiOrWeb" — either scheme accepted (for shared endpoints)
```

**New file**: `src/OpenBrain.Host/Auth/ApiKeyAuthHandler.cs`
- Validates `X-API-Key` header against `config.ApiKey`, `config.IngestorApiKey`, or `config.McpApiKey`

**New file**: `src/OpenBrain.Host/Auth/LoginModel.cs`
- Username/password DTO for login form

#### Step C.5: Setup Wizard (Blazor pages)

**New directory**: `src/OpenBrain.Host/Components/Setup/`

The setup wizard is a series of Blazor Server pages served only when `!config.SetupCompleted`. A middleware redirects all non-setup routes to `/setup` during first run.

**Pages**:

1. **`/setup`** — Welcome + Database Choice
   - Radio buttons: SQLite (recommended for single user) / PostgreSQL (for teams/advanced)
   - If SQLite: auto-populate path as `%ProgramData%\OpenBrain\openbrain.db`
   - If PostgreSQL: show connection string input + "Test Connection" button

2. **`/setup/server`** — Server Configuration
   - Port number (default 5080, validate available)
   - Enable/disable ingestor (checkbox, default on)
   - Enable/disable vector search (checkbox, default on)

3. **`/setup/credentials`** — Credentials
   - Web UI username (required, min 3 chars)
   - Web UI password (required, min 8 chars) + confirm
   - Auto-generated API keys shown (with copy buttons):
     - Primary API Key (for general use)
     - Ingestor API Key (pre-filled into ingestor config)
     - MCP API Key (for Claude MCP config)
   - "Regenerate" button for each key

4. **`/setup/sources`** — Source Detection (if ingestor enabled)
   - Auto-detect `~/.claude/projects` and `~/.codex`
   - For each: enable/disable toggle, work/personal segment picker
   - Client ID field (default: machine name)

5. **`/setup/confirm`** — Summary + Finish
   - Show all choices in a summary card
   - "Complete Setup" button
   - On click:
     1. Save `config.json`
     2. Initialize database (create tables, FTS, vector)
     3. Set `SetupCompleted = true`
     4. Restart the host to apply full configuration
     5. Redirect to login page

**New file**: `src/OpenBrain.Host/Middleware/SetupRedirectMiddleware.cs`
- If `!config.SetupCompleted` and path doesn't start with `/setup` or `/_blazor` or `/_framework`, redirect to `/setup`

#### Step C.6: Login page

**New file**: `src/OpenBrain.Host/Components/Pages/Login.razor`

- Simple form: username + password
- On submit: validate against `ConfigStore.VerifyPassword()`
- On success: issue authentication cookie, redirect to `/`
- On failure: show error message

#### Step C.7: Ingestor as hosted service

**New file**: `src/OpenBrain.Host/Services/InProcessIngestorService.cs`

When `config.IngestorEnabled`, the ingestor runs as an `IHostedService` inside the same process. This:
- Reuses the existing `Worker` class from `OpenBrain.Ingestor`
- Configures it with settings from `OpenBrainConfig` (mapped to `IngestorConfig`)
- Uses the in-process API directly (no HTTP needed — inject repositories directly)
- Falls back to HTTP client for backward compatibility if configured

Actually — for simplicity and to avoid tight coupling, the in-process ingestor will still POST to `http://localhost:{port}/api/ingest/batch` via HttpClient. This reuses all existing validation and idempotency logic.

---

### Phase D: Web UI (Blazor Server)

#### Step D.1: Blazor project structure

```
src/OpenBrain.Host/
├── Components/
│   ├── _Imports.razor              — Global usings
│   ├── App.razor                   — Root component with Routes
│   ├── Routes.razor                — Router component
│   ├── Layout/
│   │   ├── MainLayout.razor        — Sidebar nav + content area
│   │   ├── MainLayout.razor.css    — Scoped styles
│   │   └── NavMenu.razor           — Navigation sidebar
│   ├── Pages/
│   │   ├── Dashboard.razor         — Overview: counts, recent sessions, health
│   │   ├── Sessions.razor          — Session list with filters
│   │   ├── SessionDetail.razor     — Single session: messages, tool calls, artifacts
│   │   ├── Search.razor            — Full-text search with filters
│   │   ├── Logs.razor              — Ingestion log viewer
│   │   ├── Metrics.razor           — Counts, costs, token usage charts
│   │   ├── Settings.razor          — Config editor (API keys, sources, etc.)
│   │   ├── Login.razor             — Login form
│   │   └── Error.razor             — Error page
│   └── Setup/
│       ├── SetupWizard.razor       — Multi-step wizard container
│       ├── SetupDatabase.razor     — Step 1: DB choice
│       ├── SetupServer.razor       — Step 2: Port, features
│       ├── SetupCredentials.razor  — Step 3: User + API keys
│       ├── SetupSources.razor      — Step 4: Ingestor sources
│       └── SetupConfirm.razor      — Step 5: Summary + finish
├── wwwroot/
│   ├── css/
│   │   └── app.css                 — Global styles (clean, modern design)
│   └── favicon.ico
├── Services/
│   ├── SessionQueryService.cs      — Query sessions with filters (project, segment, date range, source)
│   ├── MetricsService.cs           — Aggregate metrics (token counts, costs, session counts)
│   ├── LogService.cs               — Query ingestion logs with pagination
│   └── DashboardService.cs         — Dashboard aggregate data
```

#### Step D.2: Dashboard page (`/`)

Displays:
- **Stats cards**: Total sessions, Total messages, Total tool calls, Total artifacts
- **Recent sessions**: Last 10 sessions with title, project, source, segment tag, timestamp
- **Ingestion health**: Last successful ingest time, outbox queue size, checkpoint count
- **Quick search** bar

Data source: `DashboardService` queries repositories for counts and recent items.

#### Step D.3: Sessions page (`/sessions`)

Features:
- **Filter bar**:
  - Project dropdown (populated from distinct projects in DB)
  - Segment filter: All / Work / Personal (from session tags)
  - Source filter: All / claude-code / codex-cli
  - Date range picker (from/to)
  - Search text input
- **Session table**:
  - Columns: Title, Project, Source, Segment, Messages (count), Created, Updated
  - Click row → navigate to `/sessions/{id}`
- **Pagination**: 25 per page

Data source: `SessionQueryService.ListSessionsAsync(filters, page, pageSize)`

#### Step D.4: Session Detail page (`/sessions/{id}`)

Displays:
- **Header**: Session title, project, repo, branch, source, segment tags, timestamps
- **Messages tab**: Chronological list of messages with role badges (user/assistant/system/tool), provider, model, token counts, cost
- **Tool Calls tab**: List of tool calls with name, arguments (collapsible JSON), result (collapsible JSON)
- **Artifacts tab**: List of artifacts with type, path/URL, hash, metadata

#### Step D.5: Search page (`/search`)

Features:
- **Search input** with real-time query
- **Filters**: Project, Source, Segment, Date range
- **Results list**: Each result shows:
  - Session title + link
  - Highlighted snippet (from FTS)
  - Relevance rank
  - Message role, timestamp
- **Pagination**

Data source: `SearchRepository.SearchAsync(query, filters)`

#### Step D.6: Logs page (`/logs`)

Displays:
- **Ingestion log table**:
  - Columns: Timestamp, Source, Event Type, Status, Idempotency Key
  - Filterable by source, event type, status
  - Pagination: 50 per page
- **Application log viewer**:
  - Read last N lines from NLog file target
  - Auto-refresh toggle (every 5 seconds)
  - Log level filter (Info, Warning, Error)

Data source: `LogService` — queries `IngestionLogEntry` table + reads NLog log files.

#### Step D.7: Metrics page (`/metrics`)

Displays:
- **Summary cards**: Total sessions, messages, tool calls, artifacts, ingestion events
- **Token usage**: Total tokens in/out, by provider, by model (table + simple bar chart)
- **Cost breakdown**: Total cost, by provider, by model (table)
- **Ingestion stats**: Events ingested, failed, duplicates (from ingestion log)
- **Sessions over time**: Count per day/week (simple table or ASCII-style chart)
- **Top projects**: Sessions per project
- **Segment breakdown**: Work vs Personal counts

Data source: `MetricsService` — aggregate queries against repositories.

> Note: Charts will use simple HTML/CSS bar charts or an SVG approach. No JavaScript charting library needed — keep it lightweight.

#### Step D.8: Settings page (`/settings`)

Features:
- **General**: Port, database info (read-only display of current provider + path/connection)
- **API Keys**: View/regenerate API key, Ingestor key, MCP key (with copy buttons)
- **Ingestor Sources**: Add/edit/remove sources, toggle enabled, set segment
- **Redaction**: Mode selector, exclusion list
- **Web Credentials**: Change password form
- **MCP Config**: Show the JSON snippet to add to Claude's MCP config file
- **Save button**: Writes to config.json, offers to restart services

---

### Phase E: Update Existing Projects

#### Step E.1: Update OpenBrain.Data.csproj

Add:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
```

#### Step E.2: Modify OpenBrainDbContext

- Accept `DatabaseProvider` as constructor parameter or via options
- In `OnModelCreating`:
  - If PostgreSQL: apply existing snake_case mappings, tsvector, GIN indexes
  - If SQLite: apply standard mappings, skip tsvector, use standard indexes
- The `ContentTsvector` column on `Message` entity:
  - PostgreSQL: kept as-is
  - SQLite: ignored (FTS handled externally via FTS5 virtual table)

#### Step E.3: Update SessionRepository for SQLite array handling

The Session entity has `Tags` as `List<string>`.
- PostgreSQL: stored as `TEXT[]` column
- SQLite: stored as JSON text column (e.g., `["work"]`), with EF Core value converter

**New file**: `src/OpenBrain.Data/Converters/StringListConverter.cs`
- `ValueConverter<List<string>, string>` that serializes/deserializes JSON arrays
- Applied conditionally for SQLite provider only

#### Step E.4: Update IngestorConfig cross-reference

The `OpenBrain.Host` needs to map `OpenBrainConfig` ingestor fields to `IngestorConfig`. Add a helper:

**New file**: `src/OpenBrain.Host/Services/ConfigMapper.cs`
```csharp
public static IngestorConfig ToIngestorConfig(OpenBrainConfig config)
{
    return new IngestorConfig
    {
        OpenBrainApiBaseUrl = $"http://localhost:{config.Port}",
        ApiKey = config.IngestorApiKey,
        ClientId = config.ClientId,
        ScanIntervalSeconds = config.ScanIntervalSeconds,
        BatchSize = config.BatchSize,
        Sources = config.Sources,
        Redaction = config.Redaction,
    };
}
```

#### Step E.5: Add to solution

**File**: `src/OpenBrain.slnx` — add `OpenBrain.Host` project.

---

### Phase F: NLog Configuration for Embedded Host

#### Step F.1: NLog config

**New file**: `src/OpenBrain.Host/nlog.config`

Targets:
- File target: `%ProgramData%\OpenBrain\logs\openbrain-{shortdate}.log`
- Console target: for development
- In-memory target: for web UI log viewer (ring buffer of last 1000 entries)

The in-memory target uses NLog's `MemoryTarget` or a custom `ConcurrentQueue<LogEventInfo>` that the `LogService` reads.

---

## 4. New Files Summary

| # | File | Purpose |
|---|------|---------|
| 1 | `src/OpenBrain.Models/Configuration/OpenBrainConfig.cs` | Unified config model |
| 2 | `src/OpenBrain.Models/Configuration/ConfigStore.cs` | Config file I/O, key generation, password hashing |
| 3 | `src/OpenBrain.Data/Search/SqliteFtsManager.cs` | SQLite FTS5 create/query/index |
| 4 | `src/OpenBrain.Data/Search/VectorSearchManager.cs` | Vector table creation (pgvector + sqlite-vec) |
| 5 | `src/OpenBrain.Data/DatabaseInitializer.cs` | Auto-create schema for either provider |
| 6 | `src/OpenBrain.Data/Converters/StringListConverter.cs` | `List<string>` ↔ JSON for SQLite |
| 7 | `src/OpenBrain.Host/OpenBrain.Host.csproj` | Project file |
| 8 | `src/OpenBrain.Host/Program.cs` | Unified entry point |
| 9 | `src/OpenBrain.Host/appsettings.json` | Minimal bootstrap config |
| 10 | `src/OpenBrain.Host/nlog.config` | Logging configuration |
| 11 | `src/OpenBrain.Host/Api/ApiEndpoints.cs` | REST API route mapping |
| 12 | `src/OpenBrain.Host/Auth/AuthConfiguration.cs` | Dual auth setup |
| 13 | `src/OpenBrain.Host/Auth/ApiKeyAuthHandler.cs` | API key validation handler |
| 14 | `src/OpenBrain.Host/Auth/LoginModel.cs` | Login DTO |
| 15 | `src/OpenBrain.Host/Middleware/SetupRedirectMiddleware.cs` | Force setup on first run |
| 16 | `src/OpenBrain.Host/Services/SessionQueryService.cs` | Session filtering/pagination |
| 17 | `src/OpenBrain.Host/Services/MetricsService.cs` | Aggregate statistics |
| 18 | `src/OpenBrain.Host/Services/LogService.cs` | Log querying + NLog reader |
| 19 | `src/OpenBrain.Host/Services/DashboardService.cs` | Dashboard data aggregation |
| 20 | `src/OpenBrain.Host/Services/ConfigMapper.cs` | OpenBrainConfig → IngestorConfig |
| 21 | `src/OpenBrain.Host/Services/InProcessIngestorService.cs` | Hosted ingestor wrapper |
| 22 | `src/OpenBrain.Host/Components/_Imports.razor` | Global Blazor usings |
| 23 | `src/OpenBrain.Host/Components/App.razor` | Root Blazor component |
| 24 | `src/OpenBrain.Host/Components/Routes.razor` | Router |
| 25 | `src/OpenBrain.Host/Components/Layout/MainLayout.razor` | Main layout with nav |
| 26 | `src/OpenBrain.Host/Components/Layout/MainLayout.razor.css` | Layout scoped styles |
| 27 | `src/OpenBrain.Host/Components/Layout/NavMenu.razor` | Sidebar navigation |
| 28 | `src/OpenBrain.Host/Components/Pages/Dashboard.razor` | Dashboard |
| 29 | `src/OpenBrain.Host/Components/Pages/Sessions.razor` | Session list |
| 30 | `src/OpenBrain.Host/Components/Pages/SessionDetail.razor` | Session detail view |
| 31 | `src/OpenBrain.Host/Components/Pages/Search.razor` | Full-text search |
| 32 | `src/OpenBrain.Host/Components/Pages/Logs.razor` | Log viewer |
| 33 | `src/OpenBrain.Host/Components/Pages/Metrics.razor` | Metrics dashboard |
| 34 | `src/OpenBrain.Host/Components/Pages/Settings.razor` | Settings editor |
| 35 | `src/OpenBrain.Host/Components/Pages/Login.razor` | Login form |
| 36 | `src/OpenBrain.Host/Components/Pages/Error.razor` | Error page |
| 37 | `src/OpenBrain.Host/Components/Setup/SetupWizard.razor` | Wizard container |
| 38 | `src/OpenBrain.Host/Components/Setup/SetupDatabase.razor` | DB choice step |
| 39 | `src/OpenBrain.Host/Components/Setup/SetupServer.razor` | Server config step |
| 40 | `src/OpenBrain.Host/Components/Setup/SetupCredentials.razor` | Credentials step |
| 41 | `src/OpenBrain.Host/Components/Setup/SetupSources.razor` | Source detection step |
| 42 | `src/OpenBrain.Host/Components/Setup/SetupConfirm.razor` | Summary + finish step |
| 43 | `src/OpenBrain.Host/wwwroot/css/app.css` | Global styles |
| 44 | `src/OpenBrain.Host/wwwroot/favicon.ico` | App icon |

---

## 5. Modified Files Summary

| # | File | Changes |
|---|------|---------|
| 1 | `src/OpenBrain.Data/OpenBrain.Data.csproj` | Add SQLite + BCrypt packages |
| 2 | `src/OpenBrain.Data/OpenBrainDbContext.cs` | Multi-provider OnModelCreating, conditional column mapping |
| 3 | `src/OpenBrain.Data/Repositories/SearchRepository.cs` | SQLite FTS5 search branch |
| 4 | `src/OpenBrain.Data/Repositories/IngestionRepository.cs` | SQLite-compatible SQL (no `unnest`) |
| 5 | `src/OpenBrain.Data/Repositories/SessionRepository.cs` | List<string> JSON converter for SQLite tags |
| 6 | `src/OpenBrain.Models/OpenBrain.Models.csproj` | Add BCrypt package |
| 7 | `src/OpenBrain.slnx` | Add OpenBrain.Host project |

---

## 6. Implementation Order

### Batch 1: Foundation (Steps A.1, A.2, B.1, B.2, B.3)
- Config model + store
- Database provider abstraction
- SQLite FTS5 manager

### Batch 2: Database (Steps B.4, B.5, B.6, B.7)
- Vector search infrastructure
- SearchRepository dual-provider
- IngestionRepository SQLite compatibility
- DatabaseInitializer

### Batch 3: Host Project (Steps C.1–C.4, E.5)
- Create OpenBrain.Host project
- Program.cs entry point
- API endpoint migration
- Dual authentication

### Batch 4: Setup Wizard (Step C.5, C.6)
- Setup wizard pages (5 steps)
- SetupRedirectMiddleware
- Login page

### Batch 5: Web UI Core (Steps D.1–D.4)
- Layout + navigation
- Dashboard, Sessions, Session Detail, Search

### Batch 6: Web UI Extended (Steps D.5–D.8)
- Logs, Metrics, Settings pages

### Batch 7: Ingestor Integration (Steps C.7, E.4)
- In-process ingestor hosted service
- Config mapper

### Batch 8: Polish (Step F.1, styles)
- NLog config
- CSS styling
- Final integration testing

---

## 7. User Experience Flow

### First Run
```
1. User downloads and runs OpenBrain.Host.exe
2. Console shows: "OpenBrain starting on http://localhost:5080"
3. Browser opens automatically to http://localhost:5080/setup
4. Setup Wizard:
   Step 1: "Choose your database"
           [◉ SQLite (Recommended)] [○ PostgreSQL]
           SQLite path: C:\ProgramData\OpenBrain\openbrain.db
   Step 2: "Server settings"
           Port: [5080]
           [☑] Enable background ingestor
           [☑] Enable vector search
   Step 3: "Create your account"
           Username: [admin]
           Password: [••••••••]  Confirm: [••••••••]
           API Keys (auto-generated):
             Primary:  ob-a1b2c3d4... [Copy]
             Ingestor: ob-e5f6g7h8... [Copy]
             MCP:      ob-i9j0k1l2... [Copy]
   Step 4: "Configure sources" (if ingestor enabled)
           [☑] claude-code — C:\Users\erica\.claude\projects — [personal ▼]
           [☑] codex-cli   — C:\Users\erica\.codex          — [personal ▼]
   Step 5: "All set!"
           [Summary of all choices]
           [Complete Setup]
5. Database created, config saved, app restarts
6. Redirected to login page
7. Login with chosen credentials
8. Dashboard loads with empty state
```

### Subsequent Runs
```
1. User starts OpenBrain.Host.exe (or it's already running as a service)
2. Browser opens to http://localhost:5080
3. Login page shown (if not already authenticated via cookie)
4. Dashboard shows session counts, recent activity, health status
```

---

## 8. API Backward Compatibility

The existing `OpenBrain.Api` project remains untouched. Users who prefer the standalone API + PostgreSQL setup can continue using it. The new `OpenBrain.Host` is an alternative that bundles everything.

The MCP server (`OpenBrain.Mcp`) continues to work by pointing `OPENBRAIN_API_URL` at `http://localhost:{port}/api` and using the MCP API key.

---

## 9. Verification Plan

### Setup Wizard
- [ ] Launch Host.exe with no config → browser opens to /setup
- [ ] Complete wizard with SQLite → config.json created, database file created
- [ ] Complete wizard with PostgreSQL → config.json created, schema applied
- [ ] API keys are valid format (ob-{32 hex chars})
- [ ] Password hashed with BCrypt in config.json

### Database
- [ ] SQLite: Sessions, Messages, ToolCalls, Artifacts tables exist
- [ ] SQLite: FTS5 virtual table `messages_fts` exists
- [ ] PostgreSQL: All tables + tsvector + triggers exist
- [ ] Insert session via API → appears in web UI
- [ ] Search via API and web UI → returns results with snippets

### Web UI
- [ ] Login with correct credentials → cookie issued, dashboard shown
- [ ] Login with wrong credentials → error shown
- [ ] Sessions page: filter by project, segment, source, date range
- [ ] Session detail: shows messages, tool calls, artifacts
- [ ] Search: returns highlighted results
- [ ] Logs: shows ingestion log entries
- [ ] Metrics: shows correct counts and breakdowns
- [ ] Settings: can change password, view/regenerate API keys

### Ingestor
- [ ] With ingestor enabled: creates sessions from Claude Code transcripts
- [ ] Sessions appear in web UI with correct segment tags
- [ ] Ingestor uses the generated Ingestor API key

### Build
- [ ] `dotnet build src/OpenBrain.Host` succeeds with 0 errors
- [ ] `dotnet publish src/OpenBrain.Host -c Release` produces single-directory output
- [ ] Published executable runs and serves web UI

---

## 10. Open Questions / Decisions

1. **Should the Host replace OpenBrain.Api entirely?** — Proposed: No, keep both. Host is the "easy" mode; Api is for advanced/server deployments.

2. **Should vector search embedding generation be included now?** — Proposed: No, just create the schema. Embedding generation requires an LLM API call per message and is a separate feature.

3. **Should the ingestor run in-process or as a separate service?** — Proposed: In-process by default (simplest), with option to run standalone for advanced users.

4. **HTTPS support?** — Proposed: Not in v1. The host runs on localhost. Users who need HTTPS can put it behind a reverse proxy.
