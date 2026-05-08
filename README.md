# ai-memory

ai-memory is a local code intelligence platform for AI agents. It indexes source repositories
into a SQLite database, exposes a structured query surface over HTTP and the Model Context
Protocol (MCP), and ships a Tauri desktop control panel for managing it. The goal: replace
expensive "load the whole file into context" patterns with precise, low-token symbol queries.

> **Status:** Active migration. As of 2026-05-03 the project is converging on a code-indexer-only
> scope; transcript ingestion (Claude Code, Codex CLI) is parked but not deleted. See
> [`code_indexer_mcp_system_prd.md`](./code_indexer_mcp_system_prd.md) for the product vision and
> [`.claude/memory-bank/main/plans/main-2026-05-03-tauri-code-indexer.md`](./.claude/memory-bank/main/plans/main-2026-05-03-tauri-code-indexer.md)
> for the multi-phase migration plan.

---

## Architecture

```
┌──────────────────────────────┐         ┌────────────────────────────┐
│  Tauri Desktop  (per-user)   │         │  Claude Code / IDE         │
│  React + Vite + Rust shell   │         │     │ launches per session │
│  - Controls Windows Services │         │     ▼                      │
│  - Reads runtime.json        │         │  aimemory-mcp.exe (stdio)  │
└────────────┬─────────────────┘         └────────────┬───────────────┘
             │ HTTP + X-AIMemory-Api-Key                       │ HTTP + X-AIMemory-Api-Key
             ▼                                        ▼
   ┌───────────────────────────────────────────────────────────────┐
   │  Windows Service: aimemory-api  (always running)              │
   │  ASP.NET Core minimal API on 127.0.0.1:5219                   │
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

The .NET binaries run as OS services, so indexing keeps happening when the desktop UI is
closed and Claude can call MCP tools at any time. The Tauri shell is a control panel for the
services, not their host.

---

## Distributed mode

The default deployment is single-machine — desktop + API + ingestor on one box,
one SQLite database. **Distributed mode** is an opt-in second deployment shape
where a remote machine runs only the ingestor and forwards code-index events
over the LAN to a primary AIMemory server. Use it when you have multiple dev
machines and want one centralized index.

The same NSIS installer offers two install modes on a "Setup Type" page:

- **Full install** *(default)* — desktop + both services. The primary in a
  distributed setup is a full install with remote ingestors enabled.
- **Ingestor-only (Remote node)** — only `aimemory-ingestor` plus a one-time
  pairing wizard. No local API, no dashboard.

Pairing flow: on the primary, open the desktop's **Distributed** page and toggle
**Allow remote ingestors**. The API generates a self-signed TLS cert, issues an
ingest-scoped API key, and rebinds to a LAN interface you pick from a dropdown
(or a custom IPv4). The page reveals three values — endpoint URL, API key, and
cert fingerprint — once. On the secondary, run the ingestor-only installer and
paste those three values into the wizard. The wizard pins the cert
fingerprint, authenticates, and registers the host. Subsequent ingest batches
flow over HTTPS with `X-AIMemory-Api-Key` auth.

Cross-host content dedupes — same git repo on two machines is one project, same
file content on two machines is one blob — while preserving per-host
provenance via a `file_locations` table.

End-to-end walkthrough, troubleshooting, and the security model are in
[`docs/distributed-mode-guide.md`](./docs/distributed-mode-guide.md). The
schema, wire protocol, threat model, and deferred items are in
[`docs/distributed-ingestion-design.md`](./docs/distributed-ingestion-design.md).

---

## Repository layout

| Path | What |
|---|---|
| `apps/desktop/` | Tauri 2.x desktop control panel. React+Vite frontend, Rust shell with windows-service control. |
| `src/AIMemory.Api/` | ASP.NET Core minimal API. Hosts `/api/code/*`, `/api/admin/tables/*`, `/api/ingestor/*`, plus the (parked) session/search endpoints. |
| `src/AIMemory.Ingestor/` | Worker service. Code adapter with three-tier change detection plus libgit2 tier-0. Runs as `aimemory-ingestor` Windows Service. |
| `src/AIMemory.CodeIndex/` | Parser library (C#, Python, TypeScript, Go), file filter, libgit2-backed `GitChangeDetector`. |
| `src/AIMemory.Mcp/` | MCP stdio server. Exposes `IndexFolder`, `SearchSymbols`, `GetSymbol`, etc. Reads `runtime.json` to discover the API. |
| `src/AIMemory.Models/` | Shared entities, DTOs, ingestion events (`CodeFileUpsertEvent`, `CodeFileDeleteEvent`, `CodeSymbolBatchEvent`, ...). |
| `src/AIMemory.Data/` | EF Core DbContext + repositories. SQLite default; PostgreSQL provider available but not used in the desktop bundle. |
| `src/AIMemory.Ingestor.ConfigApp/` | **Retired.** WPF Windows-only configuration utility, superseded by `apps/desktop/`. |
| `src/aimemory.client/` | **Retired (for active development).** React+Vite web SPA, kept as reference. The Tauri shell replaces it. |
| `tests/AIMemory.Tests.Unit/` | 364 unit tests (xUnit). Covers parsers, file filter, code adapter (with git mode), git change detector, API key repo, identity (host/project resolvers), TLS fingerprint, pairing repo, distributed config, ingestor sinks, and bind-interface validation. |
| `tests/AIMemory.Tests.Integration/` | 9 tests. Includes the developer-repo smoke tests (skipped when paths don't exist) and the in-process two-host distributed-ingestion smoke (`DistributedIngestionSmokeTests`). |

---

## Prerequisites

- **.NET 10 SDK** — runtime is required by users; SDK by developers
- **Node.js 20+** and npm
- **Rust** (stable, MSVC toolchain) — only needed when building the Tauri shell
- **Visual Studio Build Tools 2022** with the C++ workload — required by Rust's MSVC toolchain
- **WebView2 Runtime** — ships with Windows 10/11

---

## Quickstart (developer)

```powershell
# 1. Restore + build everything
dotnet build src/AIMemory.slnx

# 2. Run all tests (364 unit + 9 integration)
dotnet test src/AIMemory.slnx

# 3. Run the API standalone (no Tauri yet)
cd src/AIMemory.Api
dotnet run
# -> http://localhost:5219, writes runtime.json to %ProgramData%\AIMemory\Api\

# 4. Run the ingestor against a folder (separate terminal)
cd src/AIMemory.Ingestor
dotnet run
# -> reads %ProgramData%\AIMemory\Ingestor\appsettings.json for watch paths

# 5. (Optional) Run the Tauri desktop control panel
cd apps/desktop
npm install
npm run tauri dev
```

The Tauri app's Services page will show the .NET processes as "Not Installed" in dev mode
(they're console processes, not registered services). It still talks to the API via the
runtime.json the API writes.

## Quickstart (end user, when the installer ships)

```
1. Download AIMemory Desktop_<ver>_x64-setup.exe
2. Run it. The installer detects .NET 10; prompts to install if missing.
3. On the "Setup Type" page, pick:
     * Full install (default) — for a single-machine setup, or for the
       primary in a distributed setup.
     * Ingestor-only (Remote node) — for a secondary that forwards
       events to a remote primary. See docs/distributed-mode-guide.md.
4. Full install: registers `aimemory-api` and `aimemory-ingestor` as
   Windows Services (start type Automatic), starts both, opens the
   desktop dashboard.
   Ingestor-only: registers `aimemory-ingestor` only, opens the
   pairing wizard. The service starts after pairing succeeds.
5. (Full install) Click "Add folder" to index a repository.
6. (For Claude Code users) Register the MCP server:
      claude mcp add aimemory "C:\Program Files\AIMemory Desktop\AIMemory.Mcp.exe"
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

## API Endpoints (used by the Tauri shell + MCP)

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
| POST | `/api/ingest/batch` | Batch ingest events (CodeFileUpsert, CodeSymbolBatch, **CodeFileDelete**). Optional per-batch `HostId` / `ProjectId` for distributed mode; unknown `HostId` is rejected with 403. |
| GET | `/api/ingestor/recent` | Recent code-indexer events for the Tauri Services page |

### Admin (Tauri DB browser)

| Method | Path | Description |
|---|---|---|
| GET | `/api/admin/tables/{name}?limit=&offset=` | Whitelisted read-only inspector over `code_repositories`, `code_files`, `code_symbols`, `ingestion_log`, `hosts`, `projects`, `file_locations`, `pairings` |

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
| GET | `/api/stats` | Session and message totals |

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
API. Override with `AIMEMORY_API_URL` and `AIMEMORY_API_KEY` env vars for non-desktop
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
| Desktop shell | Tauri 2.x (Rust shell, React 19 + Vite frontend, NSIS installer) |
| Service control | `windows-service` Rust crate |
| Tests | xUnit, Xunit.SkippableFact for live-fire smoke tests |

---

## Status & roadmap

| Phase | Status | What |
|---|---|---|
| 0 | ✅ Done | Cross-platform service hosting, framework-dependent publish profiles, retire legacy UIs |
| 1 | ✅ Done | Git-aware Tier-0 change detection, per-file delete events, FS-mode deletion sweep |
| 2 | ✅ Done | Tauri shell scaffold, Rust SCM service control, NSIS installer with .NET prereq detection |
| 3 | ✅ Done | Admin tables endpoint, ingestor recent events feed, all UI pages |
| 4 | ✅ Done | runtime.json discovery, MCP discovery, smoke tests, this README |
| 5–11 | ✅ Done | **Distributed ingestion**: schema migration, identity layer, TLS + pairing, RemoteSink, desktop admin UI, ingestor wizard, NSIS component selection, .NET binaries bundled in the installer |
| Future | — | macOS/Linux installer parity, GitHub remote indexing, per-symbol embeddings, cert/key rotation UX |

---

## License

AGPL-3.0. See [LICENSE](./LICENSE).
