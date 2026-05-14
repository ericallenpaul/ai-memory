# AIMemory NSIS installer

Post-Tauri installer for AIMemory. Replaces `apps/desktop/src-tauri/installer/`
that was deleted in phase 12d. Bundles the three .NET services and the web SPA
into a single setup.exe; offers a **Full** primary install or an **Ingestor-only**
secondary install.

## Prerequisites

- **.NET 10 SDK** to publish the services
- **Node.js 20+** + npm to build the React SPA
- **NSIS 3.x** for `makensis.exe`. Install from <https://nsis.sourceforge.io>;
  the build script searches `PATH`, `C:\Program Files\NSIS\`, and
  `C:\Program Files (x86)\NSIS\`.
- **PowerShell 7+** to run `build-installer.ps1`.

## Build

```powershell
pwsh scripts/installer/build-installer.ps1
# -> scripts/installer/out/AIMemory_0.2.0_x64-setup.exe
```

Useful flags:

| Flag | Default | Notes |
|---|---|---|
| `-Version 0.3.0` | `0.2.0` | Embedded in the .exe name, the registry, Programs & Features. |
| `-Configuration Debug` | `Release` | Underlying `dotnet publish -c`. |
| `-OutputDir .\dist` | `scripts/installer/out` | Where the setup.exe lands. |
| `-Makensis "C:\Tools\NSIS\makensis.exe"` | auto | Skip the search. |
| `-SkipPublish` | off | Reuse whatever is already in `scripts/installer/staging/`. Speeds up iteration on the `.nsi`. |

## What the script does

1. Publishes each .NET project framework-dependent + single-file:
   - `AIMemory.Api` → `staging/Api/` — this triggers the `PublishRunWebpack`
     MSBuild target in `AIMemory.Api.csproj`, which runs `npm install` + `npm
     run build` in `src/aimemory.client/` and copies `dist/*` into
     `staging/Api/wwwroot/`. No separate SPA build is needed.
   - `AIMemory.Ingestor` → `staging/Ingestor/`
   - `AIMemory.Mcp` → `staging/Mcp/`
2. Stages license + README + the `Open-AIMemory.cmd` launcher + the
   `Pair-AIMemoryIngestor.ps1` headless pairing script.
3. Runs `makensis.exe scripts/installer/installer.nsi` with `/DVERSION`,
   `/DSTAGE_DIR`, `/DOUTPUT_FILE`.

## What the installer does

**Common:**
- Detects .NET 10 via `dotnet --list-runtimes`. If missing, opens the .NET
  download page and aborts.
- Writes `HKLM\Software\AIMemory\InstallMode` so secondaries can detect they
  shouldn't try to host the web UI.
- Drops the headless pairing script into `<INSTDIR>\scripts\` so even Full
  installs can re-pair if needed.

**Full install (primary):**
- Copies `Api/`, `Ingestor/`, `Mcp/`, `wwwroot/` to `<INSTDIR>`.
- Registers `aimemory-api` and `aimemory-ingestor` as Windows Services with
  `start= auto` via `sc.exe create`.
- Starts both services.
- Drops a Start Menu shortcut `AIMemory` that runs `Open-AIMemory.cmd`,
  which reads `%ProgramData%\AIMemory\Api\port` and opens
  `http://localhost:<port>/` in the default browser.

**Ingestor-only install (secondary):**
- Copies only `Ingestor/` and the pairing script.
- Registers `aimemory-ingestor` with `start= auto`. **Does not start it** —
  the config is empty, so the service would just fail. The pairing script
  starts it on success.
- Drops a Start Menu shortcut `Pair Ingestor` that runs
  `powershell.exe -ExecutionPolicy Bypass -File <INSTDIR>\scripts\Pair-AIMemoryIngestor.ps1`.

**Uninstall:**
- Stops and deletes both services (best-effort).
- Removes `<INSTDIR>` and Start Menu entries.
- **Preserves `%ProgramData%\AIMemory\`** so an upgrade reinstall keeps the
  database, install salt, port file, and TLS cert. Users who want a clean
  slate delete that directory manually.

## Pairing script

`Pair-AIMemoryIngestor.ps1` replaces the Tauri pairing wizard. It:

1. Prompts (or accepts as parameters) the three primary values: endpoint URL,
   API key, cert fingerprint.
2. Installs an `HttpClientHandler` server-cert callback that compares the
   leaf cert's SHA-256 against the pasted fingerprint — any mismatch aborts
   before any HTTP request goes out.
3. Calls `GET /api/health` with the API key in `X-AIMemory-Api-Key`. A 200
   means TLS pin + auth both validated.
4. Reads the local host_id via `AIMemory.Ingestor.exe --print-host-id`.
5. POSTs to `/api/pairings` with host_id + friendly name + os kind + version.
6. Writes `%ProgramData%\AIMemory\Ingestor\appsettings.json` with the
   `Ingestor.Mode = "Remote"` + `Remote.{Endpoint,ApiKey,PinnedCertFingerprint}`
   triple.
7. Runs `sc.exe start aimemory-ingestor`.

Supports `-NonInteractive` for scripted deployment (group policy / DSC).

## Layout after install

```
C:\Program Files\AIMemory\
├── Api\
│   ├── AIMemory.Api.exe                  (framework-dependent single-file)
│   ├── appsettings.json
│   ├── nlog.config
│   ├── e_sqlite3.dll                     (SQLite native)
│   ├── git2-3f4182d.dll                  (libgit2 native)
│   ├── ...                               (other natives + framework refs)
│   └── wwwroot\                          (SPA dist)
│       ├── index.html
│       └── assets\
├── Ingestor\
│   ├── AIMemory.Ingestor.exe
│   ├── appsettings.json
│   └── nlog.config
├── Mcp\
│   ├── AIMemory.Mcp.exe                  (per-session, not a service)
│   └── appsettings.json
├── scripts\
│   └── Pair-AIMemoryIngestor.ps1
├── Open-AIMemory.cmd
├── license.txt
├── README.txt
└── Uninstall.exe

C:\ProgramData\AIMemory\
├── Api\
│   ├── install-salt.bin                  (shared with Ingestor for host_id parity)
│   ├── port                              (chosen on first start)
│   ├── runtime.json                      (MCP discovery)
│   ├── tls.pfx                           (distributed mode only)
│   └── distributed.json                  (bind interface, enabled flag, fingerprint)
├── Ingestor\
│   ├── appsettings.json                  (written by Pair-AIMemoryIngestor.ps1)
│   └── checkpoints.json
├── db\
│   └── aimemory.db                       (WAL-mode SQLite)
└── logs\
```
