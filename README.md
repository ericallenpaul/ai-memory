# ai-memory

ai-memory is a local code intelligence platform for AI agents. It indexes source repositories
into a SQLite database, exposes a structured query surface over HTTP and the Model Context
Protocol (MCP), and ships a web-based control panel served by the API itself. The goal: replace
expensive "load the whole file into context" patterns with precise, low-token symbol queries.

> **Status:** As of 2026-05-14 (phase 12) the project has migrated off the Tauri desktop shell
> back to a Kestrel + React web UI served directly from `wwwroot`. The web client lives at
> `src/aimemory.client/` and is the single source of truth for the admin and user surface.
> See [`code_indexer_mcp_system_prd.md`](./code_indexer_mcp_system_prd.md) for the product vision.

---

## Architecture

```
┌──────────────────────────────┐         ┌────────────────────────────┐
│  Browser (any user)          │         │  Claude Code / IDE         │
│  React SPA from same origin  │         │     │ launches per session │
│  Cookie auth via /api/auth   │         │     ▼                      │
│                              │         │  aimemory-mcp.exe (stdio)  │
└────────────┬─────────────────┘         └────────────┬───────────────┘
             │ HTTPS or HTTP (loopback)                       │ HTTP + X-AIMemory-Api-Key
             ▼                                        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │  Windows Service: aimemory-api  (always running)              │
   │  ASP.NET Core minimal API on 127.0.0.1:<port>                 │
   │  Serves the SPA from wwwroot in production                    │
   │  Writes %ProgramData%\AIMemory\Api\runtime.json on startup    │
   └─────────────────────────────┬─────────────────────────────────┘
                                 │ POST /api/ingest/batch
                                 │
   ┌─────────────────────────────┴─────────────────────────────────┐
   │  Windows Service: aimemory-ingestor  (always running)         │
   │  - Git tier-0 (libgit2) for changed-file detection            │
   │  - mtime/size/hash fallback for non-git folders               │
   │  - Per-file delete events for git deletes                     │
   │  - End-of-cycle reconciliation for FS-mode deletions          │
   └─────────────────────────────┬─────────────────────────────────┘
                                 │
                                 ▼
                       SQLite (WAL) — %ProgramData%\AIMemory\db\aimemory.db
```

The .NET binaries run as OS services, so indexing keeps happening when the browser is closed
and Claude can call MCP tools at any time. The web UI is a control panel for the services, not
their host — on a primary install it opens to `https://localhost:<port>` and shares the
auth-cookie session with `/api/*` calls.

---

## Distributed mode

The default deployment is single-machine — API + ingestor + web UI all on one box, one SQLite
database. **Distributed mode** is an opt-in second deployment shape where a remote machine runs
only the ingestor and forwards code-index events over the LAN to a primary AIMemory server. Use
it when you have multiple dev machines and want one centralized index.

The same NSIS installer offers two install modes on a "Setup Type" page:

- **Full install** *(default)* — API + Ingestor services + web UI assets. The primary in a
  distributed setup is a full install with remote ingestors enabled.
- **Ingestor-only (Remote node)** — only `aimemory-ingestor` plus a one-time headless CLI
  pairing wizard. No local API, no dashboard.

Pairing flow: on the primary, open the web UI's **Distributed** page and toggle **Allow remote
ingestors**. The API generates a self-signed TLS cert, issues an ingest-scoped API key, and
rebinds to a LAN interface you pick from a dropdown (or a custom IPv4). The page reveals three
values — endpoint URL, API key, and cert fingerprint — once. On the secondary, run the
ingestor-only installer; on first launch the ingestor's headless wizard prompts for those three
values, pins the cert fingerprint, authenticates, and registers the host. Subsequent ingest
batches flow over HTTPS with `X-AIMemory-Api-Key` auth.

Cross-host content dedupes — same git repo on two machines is one project, same file content
on two machines is one blob — while preserving per-host provenance via a `file_locations`
table.

End-to-end walkthrough, troubleshooting, and the security model are in
[`docs/distributed-mode-guide.md`](./docs/distributed-mode-guide.md). The schema, wire
protocol, threat model, and deferred items are in
[`docs/distributed-ingestion-design.md`](./docs/distributed-ingestion-design.md).

---

## Repository layout

| Path | What |
|---|---|
| `src/AIMemory.Api/` | ASP.NET Core minimal API. Hosts `/api/code/*`, `/api/admin/*`, `/api/ingestor/*`, plus the (parked) session/search endpoints. Serves the SPA from `wwwroot/` in production and proxies to Vite via SpaProxy in development. |
| `src/AIMemory.Ingestor/` | Worker service. Code adapter with three-tier change detection plus libgit2 tier-0. Runs as `aimemory-ingestor` Windows Service. |
| `src/AIMemory.CodeIndex/` | Parser library (C#, Python, TypeScript, Go), file filter, libgit2-backed `GitChangeDetector`. |
| `src/AIMemory.Mcp/` | MCP stdio server. Exposes `IndexFolder`, `SearchSymbols`, `GetSymbol`, etc. Reads `runtime.json` to discover the API. |
| `src/AIMemory.Identity/` | `HostIdProvider`, `ProjectIdResolver`, `InstallSaltStore`. Salt directory is shared between API and Ingestor in single-machine installs so both compute the same `host_id`. |
| `src/AIMemory.Models/` | Shared entities, DTOs, ingestion events (`CodeFileUpsertEvent`, `CodeFileDeleteEvent`, `CodeSymbolBatchEvent`, …). |
| `src/AIMemory.Data/` | EF Core DbContext + repositories. SQLite default; PostgreSQL provider available but not used in the desktop bundle. |
| `src/aimemory.client/` | **Active web client.** React 19 + Vite 6 + Inter typography. Light theme default, design tokens follow [`DESIGN.md`](./DESIGN.md) and [`mockup.png`](./mockup.png). The API's `<SpaRoot>` points here. |
| `src/AIMemory.Ingestor.ConfigApp/` | **Retired.** WPF Windows-only configuration utility, kept on disk for reference but not in the active build path. |
| `tests/AIMemory.Tests.Unit/` | 378 unit tests (xUnit). Covers parsers, file filter, code adapter (with git mode), git change detector, API key repo, identity (host/project resolvers), TLS fingerprint, pairing repo, distributed config, ingestor sinks, bind-interface validation, and `sc.exe` output parsing for the post-Tauri service control endpoints. |
| `tests/AIMemory.Tests.Integration/` | Includes the developer-repo smoke tests (skipped when paths don't exist) and the in-process two-host distributed-ingestion smoke (`DistributedIngestionSmokeTests`). |

---

## Prerequisites

- **.NET 10 SDK** — runtime is required by users; SDK by developers
- **Node.js 20+** and npm — for the SPA build

---

## Quickstart (developer)

```powershell
# 1. Restore + build everything
dotnet build src/AIMemory.slnx

# 2. Run all tests
dotnet test src/AIMemory.slnx

# 3. Run the API in development (auto-launches Vite via SpaProxy when set up
#    through Visual Studio; for raw dotnet run, start Vite yourself).
cd src/AIMemory.Api
dotnet watch --launch-profile http
# -> API on http://localhost:5219, writes runtime.json to %ProgramData%\AIMemory\Api\

# 4. (Separate terminal) Start the Vite dev server for the SPA.
cd src/aimemory.client
npm install
npm run dev
# -> SPA on http://localhost:5173, proxies /api/* to the API

# 5. (Separate terminal) Run the ingestor against a folder.
cd src/AIMemory.Ingestor
$env:AIMEMORY_API_URL = "http://localhost:5219"
dotnet run
# -> reads %ProgramData%\AIMemory\Ingestor\appsettings.json for watch paths
#    (or its source-controlled appsettings.json in dev)
```

Browse to **http://localhost:5173**. The API self-registers its local host on first run, so
the loopback ingestor starts shipping events immediately.

The Services page will show the .NET processes as "NotInstalled" in dev mode (they're console
processes, not registered services). It still talks to them via the API.

## Quickstart (end user, when the installer ships)

```
1. Download AIMemory_<ver>_x64-setup.exe
2. Run it. The installer detects .NET 10; prompts to install if missing.
3. On the "Setup Type" page, pick:
     * Full install (default) — for a single-machine setup, or for the
       primary in a distributed setup.
     * Ingestor-only (Remote node) — for a secondary that forwards
       events to a remote primary. See docs/distributed-mode-guide.md.
4. Full install: registers `aimemory-api` and `aimemory-ingestor` as
   Windows Services (start type Automatic), starts both, opens the
   browser at the configured URL.
   Ingestor-only: registers `aimemory-ingestor` only, runs the headless
   pairing wizard. The service starts after pairing succeeds.
5. (Full install) Click "Add folder" on the Repositories page to index a
   repository.
6. (For Claude Code users) Register the MCP server:
      claude mcp add aimemory "C:\Program Files\AIMemory\AIMemory.Mcp.exe"
```

---

## Change detection (Phase 1 highlight)

The ingestor's code adapter has a layered change-detection pipeline:

| Layer | Mechanism | Runs when |
|---|---|---|
| **Tier 0 — Git** | `libgit2 diff` against last-seen HEAD + working-tree dirty list | Watch path is a git repo + `DetectionMode != FsOnly` |
| **Tier 0 — FS walk** | `Directory.EnumerateFiles` + filter | Watch path is **not** a git repo, or `DetectionMode = FsOnly` |
| **Tier 1** | `FileInfo.LastWriteTimeUtc` + `Length` vs checkpoint | Always |
| **Tier 2** | SHA-256 content hash vs checkpoint | Tier 1 says "maybe changed" |
| **Tier 3** | Language parser → symbols | Tier 2 confirms content actually differs |

Worst case (no `.git` folder) is the previous mtime/size/hash behavior unchanged. Best case
(clean git repo with one commit since last scan) is one libgit2 call returning a handful of
changed paths.

**Deletions:**
- Git mode: libgit2 surfaces deletions via `git diff --diff-filter=D`. Per-file
  `CodeFileDeleteEvent` emitted, API cascades to symbols.
- FS mode: end-of-cycle sweep. Filesystem is the boss — anything in the previous scan's
  enumerated set that's no longer on disk gets deleted.

Configurable per source in `appsettings.json`:

```json
{
  "Ingestor": {
    "Sources": [
      {
        "Name": "code-index",
        "Enabled": true,
        "WatchPaths": ["C:\\Users\\you\\Source\\repos\\my-project"],
        "DetectionMode": "Auto"
      }
    ]
  }
}
```

`DetectionMode` accepts `"Auto"` (default), `"GitOnly"`, or `"FsOnly"`.

---

## API Endpoints (used by the web UI + MCP)

### Code Index

| Method | Path | Description |
|---|---|---|
| POST | `/api/code/index-folder` | Index a local folder; returns repo info |
| GET | `/api/code/repos` | List indexed repositories |
| GET | `/api/code/repos/{id}/tree` | File tree |
| GET | `/api/code/repos/{id}/outline?file=` | Symbol outline for a file |
| GET | `/api/code/repos/{id}/symbol?key=` | Source slice for a symbol (byte-offset extract) |
| GET | `/api/code/search/symbols?q=` | Search symbols by name |
| DELETE | `/api/code/repos/{id}` | Remove repo from index |
| POST | `/api/code/repos/{id}/reindex` | Force re-index |

### Ingestion

| Method | Path | Description |
|---|---|---|
| POST | `/api/ingest/batch` | Batch ingest events (CodeFileUpsert, CodeSymbolBatch, **CodeFileDelete**). Optional per-batch `HostId` / `ProjectId` for distributed mode; unknown `HostId` is rejected with 403. Intra-batch duplicate `IdempotencyKey`s are treated as duplicates instead of poisoning the DbContext. |
| GET | `/api/ingestor/recent` | Recent code-indexer events for the Services dashboard |

### Admin

| Method | Path | Description |
|---|---|---|
| GET | `/api/admin/tables/{name}?limit=&offset=` | Whitelisted read-only inspector over `code_repositories`, `code_files`, `code_symbols`, `ingestion_log`, `hosts`, `projects`, `file_locations`, `pairings` |
| GET | `/api/admin/services/{name}/status` | Cross-platform service-status query (`sc.exe query` on Windows). Whitelisted to `aimemory-api` and `aimemory-ingestor`. |
| POST | `/api/admin/services/{name}/{start\|stop\|restart}` | Lifecycle commands matching the Tauri shell's pre-12 surface. Windows-first; non-Windows returns "not implemented." |
| GET | `/api/admin/network-interfaces` | Up, non-loopback IPv4 unicast addresses for the Distributed enable-flow picker. Always seeds the `0.0.0.0` wildcard so the dropdown is never empty. |
| POST | `/api/admin/fs/validate-path` | `{ path }` → `{ path, exists, isDirectory, isGitRepo }`. Replaces the native folder picker in the Repos add flow. |

### Distributed (primary admin)

| Method | Path | Description |
|---|---|---|
| POST | `/api/admin/distributed/enable?bindInterface=<ip>` | Generate cert, issue ingest key, set bind. See [`docs/distributed-mode-guide.md`](./docs/distributed-mode-guide.md). |
| POST | `/api/admin/distributed/disable` | Restore loopback bind; keep cached fingerprint. |
| GET | `/api/admin/distributed/status` | Current bind interface, port, fingerprint, paired-host count. |
| POST | `/api/pairings` | Secondary registers itself (host id, friendly name, OS kind, version). |
| GET | `/api/pairings` | List paired hosts. |
| DELETE | `/api/pairings/{id}` | Revoke a pairing and cascade-revoke its api key. |

### Health / Stats

| Method | Path | Description |
|---|---|---|
| GET | `/api/health` | DB connectivity check |
| GET | `/api/stats` | Session and message totals + daily series |

---

## MCP Tools (for Claude Code, Codex, etc.)

| Tool | Description |
|---|---|
| `IndexFolder` | Index a local folder; returns repo info with file and symbol counts |
| `ListCodeRepos` | List all indexed repositories |
| `GetFileTree` | All files in a repo with language and size |
| `GetFileOutline` | All symbols in a specific file |
| `GetRepoOutline` | High-level overview of a repo |
| `GetSymbol` | Full source of a symbol by key (`filepath::QualifiedName#kind`) — byte-offset extracted |
| `GetSymbols` | Batch retrieval |
| `SearchSymbols` | Search by name with optional kind filter |
| `SearchCodeText` | Full-text search within indexed files |
| `RemoveCodeRepo` | Delete a repo from the index |

The MCP server reads `%ProgramData%\AIMemory\Api\runtime.json` on startup to find the local
API. Override with `AIMEMORY_API_URL` and `AIMEMORY_API_KEY` env vars for non-default
deployments.

---

## Tech Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 10 (minimal API) |
| Hosting | Windows Service (`UseWindowsService`) + systemd (`UseSystemd`) — same code, both targets |
| ORM | Entity Framework Core 10 |
| Database | SQLite (default, WAL mode), PostgreSQL provider available |
| Code parsing | Roslyn (C#), regex/line-based (Python, TypeScript, Go) |
| Change detection | libgit2 via LibGit2Sharp 0.31 |
| MCP | ModelContextProtocol .NET SDK |
| Web UI | React 19, Vite 6, Inter, light-theme-first design system per DESIGN.md |
| SPA hosting | Kestrel `UseStaticFiles` + `MapFallbackToFile("index.html")` from `wwwroot/`; SpaProxy in dev |
| Service control | `sc.exe` (Windows) shell-out from `AIMemory.Api.Services.ServiceControl` |
| Tests | xUnit, Xunit.SkippableFact for live-fire smoke tests |

---

## Status & roadmap

| Phase | Status | What |
|---|---|---|
| 0 | ✅ Done | Cross-platform service hosting, framework-dependent publish profiles, retire legacy UIs |
| 1 | ✅ Done | Git-aware Tier-0 change detection, per-file delete events, FS-mode deletion sweep |
| 2 | ✅ Done | Tauri shell scaffold, Rust SCM service control, NSIS installer with .NET prereq detection |
| 3 | ✅ Done | Admin tables endpoint, ingestor recent events feed, all UI pages |
| 4 | ✅ Done | runtime.json discovery, MCP discovery, smoke tests |
| 5–11 | ✅ Done | **Distributed ingestion**: schema migration, identity layer, TLS + pairing, RemoteSink, desktop admin UI, ingestor wizard, NSIS component selection, .NET binaries bundled in the installer |
| 12 | ✅ Done | **Drop Tauri**: replaced the Rust shell with a Kestrel-served React SPA. New admin endpoints (`services/*`, `network-interfaces`, `fs/validate-path`), API self-registration, shared identity salt, ported admin pages, full DESIGN.md system, new NSIS installer that bundles services + wwwroot. |
| Future | — | macOS/Linux installer parity, GitHub remote indexing, per-symbol embeddings, cert/key rotation UX |

---

## License

AGPL-3.0. See [LICENSE](./LICENSE).
