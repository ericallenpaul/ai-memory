# Plan: Tauri Desktop Shell + Git-Aware Code Indexer

**Date:** 2026-05-03
**Branch:** main
**Status:** PLAN
**Reference PRD:** code_indexer_mcp_system_prd.md
**Supersedes (UI scope):** main-2026-03-03-setup-wizard-web-ui.md

---

## Goal

Refocus ai-memory around a single, sharply-scoped product: a **local Code Intelligence Platform** that an AI assistant can query through MCP, and that a developer can drive from a **Tauri desktop app**. The .NET API and Ingestor run as **OS background services** (always on); the Tauri app is a control panel for them.

This plan:

1. Keeps the existing `AIMemory.CodeIndex`, `AIMemory.Api`, `AIMemory.Mcp`, and `AIMemory.Ingestor` (Code adapter only).
2. Retires the WPF `AIMemory.Ingestor.ConfigApp` and the existing React web UI **for active development** (left in repo, not built into the new bundle).
3. Adds a new `apps/desktop/` Tauri shell that controls (does **not** host) the API and Ingestor services, and ships a code-indexer-only UI.
4. Adds an **NSIS installer** that requires .NET 10 runtime as a prerequisite (offers to download/install if missing) and registers two Windows Services: `aimemory-api` and `aimemory-ingestor`.
5. Adds **git-aware change detection** as a new tier-0 in `CodeAdapter`, dropping I/O on untouched files in large repos.
6. Defers transcript adapters (Claude Code, Codex CLI), session/search UI, and remote/internet API exposure.

---

## Non-Goals (this plan)

- No transcript ingestion (Claude Code, Codex CLI adapters left in code, not exposed in UI).
- No remote/internet API exposure (loopback only).
- No PostgreSQL — SQLite only.
- No embeddings / semantic search.
- No multi-user / RBAC.
- No GitHub remote indexing (local folders only; git-aware ≠ remote-clone).
- No installer/auto-update polish (covered in a follow-up plan).

---

## Architecture Overview

```
┌────────────────────────────────────┐         ┌──────────────────────────────┐
│   Tauri Desktop App  (per-user)    │         │   Claude Code / IDE          │
│                                    │         │      │                       │
│   React + Vite frontend            │         │      │ launches per session  │
│   Rust shell (src-tauri):          │         │      ▼                       │
│   ▸ Service control via SCM        │         │   aimemory-mcp.exe (stdio)   │
│   ▸ Folder picker, IPC             │         │   Reads runtime.json         │
│   ▸ HTTP client → local API        │         │   to find API URL+key        │
└──────────────┬─────────────────────┘         └────────────┬─────────────────┘
               │ HTTP 127.0.0.1                             │ HTTP 127.0.0.1
               │ X-API-Key                                  │ X-API-Key
               ▼                                            ▼
       ┌────────────────────────────────────────────────────────────┐
       │  Windows Service: aimemory-api  (always running)           │
       │     ASP.NET Core minimal API, binds 127.0.0.1:5219         │
       │     Writes %ProgramData%\AIMemory\runtime.json on start    │
       └─────────────────────────────┬──────────────────────────────┘
                                     │ POST /api/ingest/batch
                                     │
       ┌─────────────────────────────┴──────────────────────────────┐
       │  Windows Service: aimemory-ingestor  (always running)      │
       │     Polls watch paths · Git tier-0 + FS fallback           │
       │     Pushes events to API over HTTP                         │
       └─────────────────────────────┬──────────────────────────────┘
                                     │
                                     ▼
                       SQLite (WAL) — %ProgramData%\AIMemory\db\aimemory.db
                       (written by API process only)
```

**Key architectural decisions:**

- **Two Windows Services** (Option 2): `aimemory-api` and `aimemory-ingestor`. Cleaner separation, matches existing process boundaries, ingestor → API communication stays HTTP-batch as today.
- **Cross-platform service equivalents**: `systemd` user units on Linux, `launchd` agents on macOS. Same control surface from Tauri.
- **.NET 10 runtime is a prerequisite**, not bundled. Installer detects it and offers to download/install if missing. Each .NET binary is ~5–10MB instead of ~200MB.
- **Installer registers services** (one-time UAC prompt). Tauri at runtime only starts/stops/queries services — no elevation needed for normal use.
- **Loopback-only API**: API binds `127.0.0.1:5219` (configurable). On startup, API writes `%ProgramData%\AIMemory\runtime.json` with `{ baseUrl, apiKey }` so Tauri and MCP can discover it.
- **All DB access through the API**: the Tauri frontend never opens SQLite directly. Single source of truth, shared with MCP.
- **MCP server is launched on demand by Claude Code** via stdio. It reads `runtime.json` to find the API. Works whether or not Tauri is open.

---

## Phase Breakdown

### Phase 0 — Scope reset & cleanup (no behavior change)

**Step 0.1** — Mark retired projects as not-in-bundle.
- Add `<IsPackable>false</IsPackable>` and a `README.md` note to:
  - `src/AIMemory.Ingestor.ConfigApp/` — superseded by Tauri shell
  - `src/aimemory.client/` — superseded by Tauri frontend (kept as reference for now)
- Do **not** delete. They stay buildable until Tauri shell reaches feature parity for ingestor control.

**Step 0.2** — Disable transcript adapters by default.
- In `IngestorConfig` defaults: only `code-index` source is enabled out of the box.
- Existing `ClaudeCodeAdapter` / `CodexCliAdapter` registrations stay; just unselected by default.

**Step 0.3** — Configure framework-dependent publish profiles.
- Add `Publish.props` with:
  - `SelfContained=false`, `RuntimeIdentifier=win-x64` (initial), `PublishSingleFile=true`, `PublishTrimmed=true`.
- Verify each of `AIMemory.Api`, `AIMemory.Ingestor`, `AIMemory.Mcp` produces a working ~5–10MB exe under `publish/win-x64/` against an installed .NET 10 runtime.
- Trim warnings: Roslyn and EF Core are generally trim-friendly in .NET 10, but watch for migration assemblies — may need `<TrimmerRootAssembly Include="AIMemory.Data" />` in the API csproj.

**Step 0.4** — Make API and Ingestor host as Windows Services.
- Add NuGet `Microsoft.Extensions.Hosting.WindowsServices` to `AIMemory.Api` and `AIMemory.Ingestor`.
- In each `Program.cs`, call `builder.Host.UseWindowsService(o => o.ServiceName = "aimemory-api"|"aimemory-ingestor")`.
- This makes them work both as console apps (dev) and as Windows Services (production) — `UseWindowsService` is a no-op when not running under SCM.
- Linux/macOS equivalents: `Microsoft.Extensions.Hosting.Systemd` (`UseSystemd()`) for systemd. macOS launchd works fine with the default console-mode host (just needs a plist).

---

### Phase 1 — Git-aware change detection in `CodeAdapter`

The current `CodeAdapter` walks every supported file under each `WatchPath` and stat-checks each one. For a 100k-file repo that's ~100k stat calls per cycle. Git-aware detection collapses this to a single `git diff` for tracked files.

**Detection chain (always layered, never replaced):**

| Layer | Mechanism | When it runs |
|---|---|---|
| **Tier 0 — Git** | `libgit2 diff` between last-seen HEAD + working-tree dirty list | Watch path **is** a git repo |
| **Tier 0 — Filesystem walk** | Enumerate all parseable files under watch path | Watch path is **not** a git repo, OR git mode disabled, OR file is git-untracked |
| **Tier 1 — mtime + size** | `FileInfo.LastWriteTimeUtc` + `Length` vs checkpoint | Always (existing) |
| **Tier 2 — SHA-256 hash** | Compute hash, compare to checkpoint | Tier 1 says "maybe changed" (existing) |
| **Tier 3 — Parse** | Run language parser, emit symbols | Tier 2 confirms content is actually different (existing) |

**The user's concern — what if there's no `.git`?** Tier 0 silently degrades to a filesystem walk; Tiers 1–3 are unchanged from the current implementation. That means the worst case is exactly today's behavior (full enumeration + mtime/size/hash), and the best case (clean git repo, one commit since last scan) is one libgit2 call returning a handful of paths. **No regression for non-git folders.**

There's also a subtle case: a git repo containing untracked files (a `*.cs` file the user hasn't `git add`-ed yet). Git's diff won't see it. The plan handles this in Step 1.5 — when in git mode, untracked-but-parseable files are merged into the changed set via `git status --porcelain` (which lists untracked paths under `??`). After that point they hit Tiers 1–3 like any other file.

**Step 1.1** — Add `LibGit2Sharp` to `AIMemory.CodeIndex`.
- NuGet: `LibGit2Sharp` (mature, no native runtime install needed; ships native libgit2 per RID).

**Step 1.2** — Add `GitChangeDetector` to `AIMemory.CodeIndex/Git/`.
```csharp
public class GitChangeDetector
{
    bool TryOpen(string watchPath, out IRepository? repo);
    GitWatchState GetCurrentState(IRepository repo);   // HEAD sha + working-tree dirty list
    IEnumerable<string> ChangedSince(IRepository repo, GitWatchState lastState);
    IEnumerable<string> EnumerateTrackedFiles(IRepository repo);
}

public record GitWatchState(string HeadSha, IReadOnlySet<string> DirtyPaths);
```

`ChangedSince` returns:
- All paths in `git diff --name-only <lastHead>..<currentHead>` (commits since last scan)
- Plus all paths in `git status --porcelain` matching modified/added/renamed (working tree changes)
- Minus deletions (ingestor removes via separate event later — see Step 1.6).

**Step 1.3** — Extend `Checkpoint` for git state.
```csharp
public class Checkpoint
{
    // existing fields...
    public string? GitHeadSha { get; set; }              // NEW
    public string[]? GitDirtyPaths { get; set; }         // NEW — last-seen working tree dirty set
}
```

Stored per **WatchPath**, not per file. Add a separate "watch-path checkpoint" alongside the existing per-file checkpoints (or namespace the key as `watchpath::<path>`).

**Step 1.4** — Add `ChangeDetectionMode` to `SourceConfig`.
```csharp
public enum ChangeDetectionMode { Auto, GitOnly, FsOnly }
public class SourceConfig
{
    // ...
    public ChangeDetectionMode DetectionMode { get; set; } = ChangeDetectionMode.Auto;
}
```

`Auto` (default) = use git when watch path is a git repo, fall back to filesystem tier-0 (mtime+size) for everything else.

**Step 1.5** — Modify `CodeAdapter.DiscoverFiles` to be git-aware.

Current behavior: walk every file under watch path, return all parseable files.

New behavior:
1. **Probe**: try to open the watch path as a git repo via `LibGit2Sharp.Repository.IsValid(path)`.
2. **Git mode** (probe succeeds, mode allows it):
   - First scan: enumerate tracked files via libgit2 + `git status --porcelain` for untracked-but-parseable. Save current `GitWatchState` to watch-path checkpoint. Yield each file.
   - Subsequent scans: compute `ChangedSince(lastState)` = `diff(lastHead..HEAD)` ∪ working-tree dirty ∪ newly-untracked. Yield only those. Unchanged files don't even get a stat call.
   - Save the new `GitWatchState` after the cycle completes successfully.
3. **Filesystem mode** (probe fails or `DetectionMode = FsOnly`):
   - Today's behavior, unchanged: walk every parseable file under the watch path. Tiers 1–3 still skip files whose mtime/size/hash hasn't changed, so the cost is one stat per file plus rare hash reads. **This is the backup the user asked about** — it kicks in automatically with no `.git` folder.
4. **Mixed within a single watch path** is not supported in this plan: a watch path is either a git repo (uses git tier) or it isn't (uses FS walk). Sub-folders inherit the parent's mode.

**Step 1.6** — Handle deletions.
- Git mode: `git diff --diff-filter=D` gives deleted paths since last scan. Emit a new `CodeFileDeleteEvent` for each.
- Add `CodeFileDeleteEvent { RepositoryName, FilePath, MachineName }` to `AIMemory.Models/Events/`.
- Handle in `/api/ingest/batch` switch: cascade-delete the file's symbols.
- FS-only mode: no deletion detection in this plan (open question §A).

**Step 1.7** — Three-tier still applies *inside* the git-changed set.

Even when git tells us a file changed, we still run the existing tier-2 hash check before parsing. This handles:
- Whitespace-only changes (still re-parse — git considers them changed; fine to re-parse)
- Files marked dirty in working tree but reverted (`git status` may say dirty even if content matches HEAD — hash check skips parse)

So order becomes: **git filter → mtime+size → hash → parse**.

**Step 1.8** — Tests for `GitChangeDetector`.
- New test project `tests/AIMemory.CodeIndex.Tests/Git/`.
- Use `LibGit2Sharp` to construct in-memory or temp-dir test repos with known commit graphs.
- Cover: clean → dirty, commits between scans, untracked file, ignored file, deleted file, rename, non-git folder.

---

### Phase 2 — Installer + Tauri desktop shell

**Step 2.1** — Scaffold the Tauri app.
- Location: `apps/desktop/` (top-level new folder; not under `src/` to keep .NET solution clean).
- Use `npm create tauri-app@latest` with React + TypeScript + Vite template.
- Tauri 2.x. Frontend runtime: same React 19 + Vite 7 stack already used in `aimemory.client`.

**Step 2.2** — Reuse existing frontend code where useful.
- Copy (don't move yet): `theme.ts`, `ThemeContext.tsx`, `Logo.tsx`, `Spinner.tsx`, the API client utilities from `src/aimemory.client/src/`.
- Drop session/search/logs/keys/setup-wizard pages — out of scope for this plan.

**Step 2.3** — Tauri capabilities and config.
- `tauri.conf.json`:
  - `bundle.active = true`, target `nsis` (Windows). Linux/macOS targets in a follow-up plan.
  - `app.windows`: single window, min 1024×700, system theme.
- Capabilities (`src-tauri/capabilities/main.json`):
  - `core:default`
  - `dialog:allow-open` (folder picker for "add watch path")
  - `fs:allow-read-text-file` scoped to `$APPDATA/AIMemory/**` and `$PROGRAMDATA/AIMemory/**`
  - `http:default` allowlist limited to `http://127.0.0.1:*`
- No `shell:allow-execute` needed — service control happens via the Windows SCM API, not by spawning processes.

**Step 2.4** — Rust crate dependencies for service control.
- `windows-service = "0.7"` — typed wrappers around Windows SCM (Open/Start/Stop/QueryStatus).
- Linux/macOS: shell out to `systemctl --user` and `launchctl` respectively (separate code paths, but same Rust trait).

**Step 2.5** — Rust commands (`src-tauri/src/lib.rs`):
```rust
#[tauri::command] fn runtime_config() -> RuntimeConfig          // reads %ProgramData%\AIMemory\runtime.json
#[tauri::command] fn service_status(name: ServiceName) -> ServiceStatus  // running/stopped/not-installed/error
#[tauri::command] async fn service_start(name: ServiceName) -> Result<()>
#[tauri::command] async fn service_stop(name: ServiceName) -> Result<()>
#[tauri::command] async fn service_restart(name: ServiceName) -> Result<()>
#[tauri::command] async fn pick_folder() -> Option<PathBuf>
#[tauri::command] async fn open_log_folder() -> Result<()>      // shell-open %ProgramData%\AIMemory\logs
```

`ServiceName` enum: `Api`, `Ingestor`. `ServiceStatus` includes state, pid (if running), uptime, last exit code.

If `service_status` returns `not-installed` (e.g., the user runs the dev build without running the installer), the UI shows a banner: "Service not installed — run installer or click 'Install services' for a one-time UAC prompt." The "Install services" button calls a small elevated helper (Step 2.7) — opt-in, not the default path.

**Step 2.6** — NSIS installer (Windows).
- Build artifact: `apps/desktop/installers/aimemory-setup-{version}.exe`.
- Generated by Tauri's NSIS bundler with custom hooks for service registration.
- Installer steps:
  1. **Detect .NET 10 runtime**. Check registry `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost`. If missing or version < 10, prompt to download from `https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe` and run it inline.
  2. **Copy binaries** to `%ProgramFiles%\AIMemory\` (api, ingestor, mcp, plus dependent DLLs).
  3. **Create data directories** `%ProgramData%\AIMemory\{db,logs,config}` with appropriate ACLs.
  4. **Register two services** via `sc.exe create`:
     ```
     sc create aimemory-api binPath= "%ProgramFiles%\AIMemory\AIMemory.Api.exe" start= auto DisplayName= "AIMemory API"
     sc create aimemory-ingestor binPath= "%ProgramFiles%\AIMemory\AIMemory.Ingestor.exe" start= auto DisplayName= "AIMemory Ingestor" depend= aimemory-api
     sc start aimemory-api
     sc start aimemory-ingestor
     ```
  5. **Install Tauri app** to `%LocalAppData%\Programs\AIMemory Desktop\` with Start Menu shortcut.
  6. **Uninstaller**: `sc stop` + `sc delete` both services, remove `%ProgramFiles%\AIMemory\`, **leave `%ProgramData%\AIMemory\` alone** (preserves DB and logs; user can manually remove).

**Step 2.7** — One-time elevated install helper (for dev / portable mode).
- Small companion exe `apps/desktop/src-tauri/install-services/install-services.exe` (Rust).
- Embedded UAC manifest (`requireAdministrator`).
- Subcommands: `install-services`, `uninstall-services`.
- Used when running Tauri from a dev build or portable install. The full NSIS installer covers production.

**Step 2.8** — Service account.
- Services run as `LocalSystem` by default (NSIS default). This works for indexing folders the user has explicit permission for.
- **Gotcha**: `LocalSystem` does *not* have access to user network drives or some user-profile paths. If indexing fails on a watch path with EACCES, the UI shows: "Service can't read this path — change service account to your user, or copy the folder under a system-readable location."
- Phase 1 ships with `LocalSystem`; service-account configuration is a follow-up if it bites in real use.

**Step 2.9** — Service config files.
- `%ProgramData%\AIMemory\config\api.json` — port, db path, log level.
- `%ProgramData%\AIMemory\config\ingestor.json` — watch paths, scan interval, detection mode per source. This is what the existing `IngestorConfig` already maps to.
- Both services watch their config file with `IOptionsMonitor` and pick up changes without a restart where possible (port changes still need restart).
- Tauri writes these files when the user changes settings. ACLs: writable by Administrators + Users (so non-admin Tauri can edit).

**Step 2.10** — On Tauri launch: read `runtime.json`, query both services' status, render dashboard. **No process spawning.** If services aren't running, UI offers "Start" buttons.

---

### Phase 3 — Code-indexer UI

All pages live under one window; route via `react-router`.

**Step 3.1** — `Repos` page (`/`).
- List of indexed repos with: name, path, file count, symbol count, last indexed, status badge.
- Actions per row: **Reindex** · **Open in Files** · **Remove**.
- Top toolbar: **Add Folder** (opens Tauri folder picker → calls `POST /api/code/index-folder`).
- Calls: `GET /api/code/repos`, `POST /api/code/repos/{id}/reindex`, `DELETE /api/code/repos/{id}`.

**Step 3.2** — `Repo Detail` page (`/repos/:repoId`).
- Left pane: file tree (`GET /api/code/repos/{id}/tree`).
- Middle pane: symbol outline of selected file (`GET /api/code/repos/{id}/outline?file=...`).
- Right pane: source preview of selected symbol (`GET /api/code/repos/{id}/symbol?key=...`).
- Top: search box → `GET /api/code/search/symbols?q=...&repo={id}`.

**Step 3.3** — `DB Browser` page (`/db`).
- Tabs: `code_repositories`, `code_files`, `code_symbols`, `ingestion_log`.
- Read-only paginated table view. Server-side paging via new endpoints (Step 3.6).
- Each row expandable to show full record JSON.
- This is the "visibility into the SQLite database" the user asked for — a power-user inspector that proves the index is correct.

**Step 3.4** — `Services` page (`/services`).
- Two cards: `aimemory-api` and `aimemory-ingestor`.
- Each shows: state (Running/Stopped/Not Installed), pid, uptime, last error from event log.
- Buttons: **Start** · **Stop** · **Restart** · **View Logs** (opens `%ProgramData%\AIMemory\logs\`).
- Watch paths editor (writes `ingestor.json`). Per path: detection mode dropdown (Auto/Git/FS), interval override. Saving prompts: "Restart ingestor service to apply?"
- Recent activity feed via `/api/ingestor/recent`.

**Step 3.5** — `Settings` page (`/settings`).
- DB path (read-only display, from `runtime.json`).
- API port (editable; writes `api.json`, requires API service restart).
- Default scan interval.
- Theme toggle.

**Step 3.6** — New API endpoints to support DB browser.
- `GET /api/admin/tables/{name}?limit=50&offset=0&orderBy=...`
  - `name` whitelist: `code_repositories`, `code_files`, `code_symbols`, `ingestion_log`.
  - Returns `{ columns: [...], rows: [...], total }`.
  - Requires `admin` scope (or cookie auth in single-user mode).
- `GET /api/ingestor/status` — proxies the ingestor's status file.
- `GET /api/ingestor/recent?limit=50` — recent ingestion log entries filtered to code events.

---

### Phase 4 — Wiring & integration

**Step 4.1** — Auth in single-user desktop mode.
- API service generates a "desktop" API key on first start if `%ProgramData%\AIMemory\runtime.json` is missing.
- Writes `runtime.json` with `{ baseUrl: "http://127.0.0.1:5219", apiKey: "aimemory_..." }` and ACL'd readable by Authenticated Users on Windows; `0640` group=aimemory on Unix.
- Tauri frontend reads via Rust `runtime_config` command and adds `X-API-Key` header to every fetch.
- The desktop key is also persisted as a regular row in the `api_keys` table with scope `admin` and a `Name = "desktop"` so it's revocable through the existing UI.

**Step 4.2** — MCP discovery.
- `aimemory.mcp.exe` reads `%ProgramData%\AIMemory\runtime.json` on startup to find API URL + key.
- Update `AIMemory.Mcp/Program.cs` to prefer `runtime.json` over env vars (env vars still win if set, for non-desktop deployments).
- Document in `README.md`: how to register the MCP server with Claude Code (`claude mcp add aimemory "C:\Program Files\AIMemory\AIMemory.Mcp.exe"`).

**Step 4.3** — End-to-end smoke test.
- Manual test plan in `apps/desktop/TESTING.md`:
  1. Run installer on a clean Windows VM without .NET → installer detects missing runtime, installs it, registers both services, starts them.
  2. Launch Tauri app → frontend reads `runtime.json`, services show "Running", empty Repos list.
  3. Add Folder → pick this repo → reindex completes → see file/symbol counts increment.
  4. Modify a `.cs` file → next ingestor cycle re-indexes only that file.
  5. `git commit` a change → next ingestor cycle picks it up via git tier (verify via `/api/ingestor/recent`).
  6. Stop API service from Tauri → Repos list shows "API offline" banner; UI degrades gracefully.
  7. From Claude Code: register MCP, call `SearchSymbols` for a known symbol, verify byte-offset slice is correct. Should work whether or not Tauri is open.
  8. Reboot the VM → both services start automatically (start= auto), MCP works without launching Tauri.
  9. Run uninstaller → services stop and unregister, `%ProgramFiles%\AIMemory\` removed, `%ProgramData%\AIMemory\` preserved.

**Step 4.4** — Update `README.md` and the PRD.
- README: replace web-UI screenshots/instructions with Tauri instructions.
- PRD (`code_indexer_mcp_system_prd.md`): change "React + Electron UI" → "React + Tauri UI"; remove Cloudflare Tunnel mention from §11; update §14 Deployment to describe Tauri bundling.

---

## File Changes Summary

### New files

| File | Purpose |
|---|---|
| `apps/desktop/` | Entire Tauri app |
| `apps/desktop/src-tauri/src/lib.rs` | Rust commands + service control |
| `apps/desktop/src-tauri/src/services.rs` | Cross-platform service control (SCM/systemd/launchd) |
| `apps/desktop/src-tauri/install-services/` | Elevated helper for dev/portable install |
| `apps/desktop/src-tauri/tauri.conf.json` | Bundle/capability config |
| `apps/desktop/src-tauri/installer/installer.nsh` | NSIS hooks: .NET check + service registration |
| `apps/desktop/src/pages/Repos.tsx` | Repo list + add folder |
| `apps/desktop/src/pages/RepoDetail.tsx` | Tree + outline + symbol view |
| `apps/desktop/src/pages/DbBrowser.tsx` | SQLite table inspector |
| `apps/desktop/src/pages/Services.tsx` | Service control (API + Ingestor) + watch paths editor |
| `apps/desktop/src/pages/Settings.tsx` | Local settings |
| `apps/desktop/scripts/build-binaries.ps1` | Publishes .NET binaries (framework-dependent, trimmed) |
| `src/Publish.props` | Shared publish profile (framework-dependent + trim) |
| `src/AIMemory.CodeIndex/Git/GitChangeDetector.cs` | Git-aware tier-0 |
| `src/AIMemory.Models/Events/CodeFileDeleteEvent.cs` | Delete event |
| `tests/AIMemory.CodeIndex.Tests/Git/GitChangeDetectorTests.cs` | Git detection tests |

### Modified files

| File | Change |
|---|---|
| `src/AIMemory.Api/Program.cs` | `UseWindowsService`; write `runtime.json` on start; add `/api/admin/tables/{name}`, `/api/ingestor/status`, `/api/ingestor/recent`, `CodeFileDelete` event handler |
| `src/AIMemory.Api/AIMemory.Api.csproj` | Add `Microsoft.Extensions.Hosting.WindowsServices` + `Microsoft.Extensions.Hosting.Systemd` |
| `src/AIMemory.Ingestor/Program.cs` | `UseWindowsService`; bind config from `%ProgramData%\AIMemory\config\ingestor.json`; FS-mode deletion sweep |
| `src/AIMemory.Ingestor/AIMemory.Ingestor.csproj` | Add `Microsoft.Extensions.Hosting.WindowsServices` + `Microsoft.Extensions.Hosting.Systemd` |
| `src/AIMemory.Ingestor/Adapters/CodeAdapter.cs` | Inject `GitChangeDetector`; add git-aware enumeration path |
| `src/AIMemory.Ingestor/Checkpointing/ICheckpointStore.cs` | Add per-watch-path checkpoint with `GitHeadSha`, `GitDirtyPaths` |
| `src/AIMemory.Ingestor/Configuration/IngestorConfig.cs` | Add `DetectionMode`; default code-index source only |
| `src/AIMemory.Mcp/Program.cs` | Read `runtime.json` for API URL + key |
| `src/AIMemory.CodeIndex/AIMemory.CodeIndex.csproj` | Add `LibGit2Sharp` package |
| `src/AIMemory.Ingestor.ConfigApp/README.md` | Mark retired |
| `src/aimemory.client/README.md` | Mark retired (kept as reference) |
| `code_indexer_mcp_system_prd.md` | Electron → Tauri; service-based deployment |
| `README.md` | Updated quickstart: install → services → Tauri |

---

## Implementation Order

| # | Phase | Step | Outcome |
|---|---|---|---|
| 1 | 0 | 0.1–0.4 | Retired projects marked; framework-dependent publish profiles; API and Ingestor wired for `UseWindowsService` (still runs as console app in dev) |
| 2 | 1 | 1.1–1.3 | LibGit2Sharp added; `GitChangeDetector` + checkpoint extension built |
| 3 | 1 | 1.4–1.7 | `CodeAdapter` integrates git tier; deletions emit events (both modes) |
| 4 | 1 | 1.8 | `GitChangeDetector` tests passing |
| 5 | 2 | 2.1–2.5 | Tauri scaffold + Rust service-control commands stubbed (using running console processes for dev) |
| 6 | 2 | 2.6–2.7 | NSIS installer registers services on a clean VM; uninstall clean |
| 7 | 2 | 2.8–2.10 | Service config files + Tauri reads `runtime.json` and queries SCM |
| 8 | 3 | 3.1–3.2 | Repos + RepoDetail pages functional against live API |
| 9 | 3 | 3.3, 3.6 | DB browser + admin endpoints |
| 10 | 3 | 3.4–3.5 | Services control + Settings pages |
| 11 | 4 | 4.1–4.4 | Auth, MCP discovery, smoke test, docs |

Each phase should end at a runnable state. Phase 1 ships independently of Tauri (improves the existing ingestor on its own); Phases 2 and 3 each ship usable increments — by end of Phase 2 the installer + services work without any UI, and Phase 3 layers the UI on top.

---

## Decisions (resolved)

- **A. Deletion detection in non-git mode** → **filesystem is the boss**. At the end of each FS-mode scan, diff the DB's file list for the repo against what was enumerated; emit `CodeFileDeleteEvent` for any DB row whose file no longer exists. Costs one query per repo per scan. Implemented in Step 1.6.
- **D. Bundle size** → **framework-dependent publish + .NET 10 prerequisite**. Installer detects/installs the runtime. Each binary drops to ~5–10MB. Trimming further is a nice-to-have, not load-bearing.
- **Service topology (Option 2)** → two separate services: `aimemory-api` and `aimemory-ingestor`. Ingestor depends on API at the SCM level (`depend= aimemory-api`).

## Open Questions

**B. Submodules / nested git repos.** A watch path containing submodules — do we treat each submodule as its own repo, or follow the parent's `git ls-files --recurse-submodules`? **Proposal:** Phase 1 ignores submodules (treats parent as single repo); revisit if it bites in real use.

**C. Renames.** `git diff -M` detects renames; libgit2 exposes them. Currently the schema would treat a rename as `delete + insert`, losing symbol identity. **Proposal:** Phase 1 accepts the loss (rename = delete old SymbolKeys, insert new). Add rename handling later if it matters.

**E. macOS / Linux.** Plan is Windows-first. Tauri shell is cross-platform; service-control needs platform-specific code paths (SCM/systemd/launchd). **Proposal:** add osx-arm64 and linux-x64 in a follow-up plan once Windows is solid.

**F. Existing web UI.** It's full of useful patterns (theme, layout, login). **Proposal:** keep `aimemory.client/` in the repo unmodified for now; revisit deletion after Tauri shell reaches feature parity.

**G. Service account.** Default install runs services as `LocalSystem`. This works for most cases but fails on user-mapped network drives and some user-profile paths. **Proposal:** ship `LocalSystem` default; add a "Service Account" setting in Phase 4 if real use surfaces failures (e.g., dropdown: LocalSystem / NetworkService / This User).

**H. API port collisions.** Default port 5219 may collide with another dev server. **Proposal:** API service tries 5219 first, falls back to ephemeral, writes the actual port to `runtime.json`. Tauri/MCP always read from the file, never assume the port.

---

## Success Criteria

1. **Installer on a clean Windows VM** detects missing .NET 10 runtime, installs it, registers `aimemory-api` and `aimemory-ingestor` services, and starts both. `GET http://127.0.0.1:5219/api/health` returns 200.
2. **Services survive reboot** — both come up automatically on next boot, and MCP works without ever launching the Tauri app.
3. **Tauri control panel** shows both services as Running, lists indexed repos, and can start/stop services without UAC prompts during normal use.
4. **First-time index** — adding this very repo indexes the .NET code, and `SearchSymbols("CodeAdapter")` from the UI returns the right symbol with byte-offset source slice.
5. **Incremental indexing** — modifying one file and committing it causes only that file to be re-indexed on the next cycle (verified via `/api/ingestor/recent`).
6. **Deletion detection** — deleting a file (in either git or FS mode) removes its symbols from the DB on the next cycle.
7. **MCP from Claude Code** — `SearchSymbols` and `GetSymbol` work end-to-end whether or not the Tauri app is open.
8. **Installer size** — under 50MB (excluding bundled .NET runtime download, which is fetched on demand if missing).
9. **Uninstaller** — stops and removes both services, removes program files, preserves `%ProgramData%\AIMemory\` (DB + logs).
