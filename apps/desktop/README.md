# AIMemory Desktop

Tauri 2.x desktop control panel for the AIMemory services. React 19 + Vite
frontend, Rust shell with `windows-service` for SCM control, NSIS installer.

The desktop is a control panel for `aimemory-api` and `aimemory-ingestor` —
not their host. The .NET binaries run as Windows Services so indexing keeps
working when the desktop window is closed. Project-wide context is in the
[root README](../../README.md).

This binary serves two faces:

- **Full mode** *(default)* — dashboard for managing indexed repos, viewing
  recent ingest events, browsing the database, and (since phase 8)
  configuring distributed mode.
- **Ingestor-only mode** — a stripped-down secondary-machine wizard for
  pairing with a remote AIMemory primary. The same Tauri binary, switched at
  runtime by `%ProgramData%\AIMemory\app-mode.json` written by the installer.

See [`../../docs/distributed-mode-guide.md`](../../docs/distributed-mode-guide.md)
for the distributed setup walkthrough.

---

## Build prerequisites

- **Node.js 20+** and npm.
- **Rust** (stable, MSVC toolchain). `rustup default stable-x86_64-pc-windows-msvc`.
- **Visual Studio Build Tools 2022** with the **C++ workload** — required by
  Rust's MSVC toolchain. The Tauri build will fail without it.
- **WebView2 Runtime** — ships with Windows 10/11.
- **.NET 10 SDK** — required to publish the API and Ingestor binaries that
  get bundled into the installer (see below).

---

## Local development

```powershell
cd apps/desktop
npm install
npm run tauri dev
```

In `tauri dev` the desktop talks to whatever local API is running. Start the
API and Ingestor in separate terminals (`dotnet run` from
`src/AIMemory.Api/` and `src/AIMemory.Ingestor/`). The Services page will
show "Not Installed" because dev-mode `dotnet run` doesn't register
Windows Services — the API connection itself works because the API still
writes `runtime.json` on startup.

To exercise distributed-mode UI without a second machine, use the in-process
two-host smoke test (`tests/AIMemory.Tests.Integration/DistributedIngestionSmokeTests.cs`)
as a reference, or run two separate API instances on different ports against
different `%ProgramData%` overrides via the `PROGRAMDATA` env var.

---

## Building the installer

```powershell
cd apps/desktop
npm run tauri:build
```

Under the hood, `npm run tauri:build` chains:

1. `npm run prepublish:dotnet` — runs `scripts/publish-dotnet.mjs`, which
   `dotnet publish`es `AIMemory.Api` and `AIMemory.Ingestor` into
   `src-tauri/bundle-resources/{api,ingestor}/`. The script blows away the
   bundle-resources tree on each run so stale binaries don't survive.
2. `tauri build` — bundles those resources via `tauri.conf.json`'s
   `bundle.resources` map (lands at `$INSTDIR\resources\{api,ingestor}\`
   after install) and emits the NSIS `.exe`.

The same chain runs from `tauri.conf.json`'s `beforeBuildCommand`, so a
plain `npm run tauri build` would also do the right thing — but the
`tauri:build` script makes the dependency explicit.

The output is at:

```
apps/desktop/src-tauri/target/release/bundle/nsis/AIMemory Desktop_<ver>_x64-setup.exe
```

A working build is ~19–20 MB. If you see a ~3 MB output, the .NET publish
step was skipped — see troubleshooting below.

---

## Troubleshooting

### `cargo build` / `tauri build` fails with linker errors

Rust's MSVC toolchain needs the VS 2022 C++ environment on `PATH`. The
common symptom is a missing `link.exe` or missing CRT libraries.

The supported invocation is to run `cargo` (or `tauri`) from a shell that
has sourced the VS 2022 vcvars64 batch file:

```powershell
cmd /c '"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" && cargo check'
```

(Adjust the path for Professional / Enterprise / BuildTools editions.)

If both VS 2022 and a newer VS preview are installed, Rust's auto-detection
may pick the preview's `link.exe`; preview editions sometimes ship without
the full CRT libs. Forcing vcvars64 from VS 2022 sidesteps this.

### Built `setup.exe` is empty / missing API binary

Symptom: the installer runs, services register, but `aimemory-api.exe`
isn't in `$INSTDIR\resources\api\` after install — or the installer file
itself is suspiciously small (~3 MB instead of ~20 MB).

Cause: `npm run prepublish:dotnet` failed silently or didn't run.

Fix: run the publish manually and inspect the output:

```powershell
cd apps/desktop
npm run prepublish:dotnet
```

The expected result is two trees under
`src-tauri/bundle-resources/`:

- `api/AIMemory.Api.exe` (~32 MB framework-dependent single-file)
- `ingestor/AIMemory.Ingestor.exe` (~33 MB)

Both directories also contain `appsettings.json`, `nlog.config`,
`web.config`, `wwwroot/`, `e_sqlite3.dll`, and `git2-*.dll`.

If `dotnet publish` itself fails, check that the .NET 10 SDK is installed
(`dotnet --list-sdks`) and that `src/Publish.props`'s framework /
target-framework values match what your SDK supports.

### Services don't start after install

Check the Windows Event Log under **Windows Logs → Application**, source
`aimemory-api` or `aimemory-ingestor`. Common causes:

- .NET 10 runtime not installed. The installer prompts for this in its
  PREINSTALL hook, but if you skipped the prompt, the service start will
  fail. Run the prompt manually: download .NET 10 from
  <https://dotnet.microsoft.com/download/dotnet/10.0> and reinstall.
- Port 5219 already in use. Check with `netstat -ano | findstr 5219`.
- (Ingestor-only mode only) `appsettings.json` missing or unwritable. The
  pairing wizard writes it; if pairing wasn't completed, the ingestor's
  config validator will fail fast and the service won't start.

### Distributed: "Test connection" reports the fingerprint doesn't match

The wizard hashes the actual leaf cert it sees during the TLS handshake
and compares it byte-for-byte to what you pasted. Causes (in order):

- Pasted the wrong line, or extra whitespace. The wizard accepts colon
  separated and plain hex but typos still win — re-copy from the primary's
  Distributed page.
- The primary regenerated its cert (rare; happens if `tls.pfx` was
  deleted). Re-copy and try again.
- Genuine MITM. Investigate.

### Distributed: "Test connection" reports unreachable

`PairError::Network` from rustls or reqwest. Check, in order:

- Primary's Distributed page is enabled (toggle reads "Allow remote
  ingestors").
- `aimemory-api` is running on the primary (`sc.exe query aimemory-api`).
- The host firewall allows inbound on the API port (5219 by default).
- The endpoint IP matches what the primary actually bound to. If you used
  `Any (0.0.0.0)`, any of the primary's IPs should work; if you specified a
  single interface, only that IP accepts connections.

### Distributed: "Test connection" reports unauthorized

The TLS pin passed but the API key was rejected. Check whitespace, then
verify the key on the primary's reveal panel hasn't been replaced (clicking
**Disable** then **Allow remote ingestors** issues a fresh key). If you've
lost the original, re-issue from the primary and re-pair.

### Distributed: ingest is rejected with HTTP 403 after pairing succeeded

The host was revoked on the primary. The ingest-batch handler now
validates `HostId` against the active hosts table on every request, even
inside the 60-second key cache window. Re-pair from the secondary's
status page using the **Re-pair** button.

### Distributed: wizard reports "could not read host ID"

The wizard shells out to `AIMemory.Ingestor.exe --print-host-id` to get
the stable per-machine host id. If the ingestor binary isn't in the
install dir (failed install, repair-pending, etc.) the flag won't resolve.
Re-run the installer and pick **Repair**.

For the full distributed-mode walkthrough and security model, see
[`../../docs/distributed-mode-guide.md`](../../docs/distributed-mode-guide.md).

---

## Recommended IDE setup

- [VS Code](https://code.visualstudio.com/) +
  [Tauri](https://marketplace.visualstudio.com/items?itemName=tauri-apps.tauri-vscode) +
  [rust-analyzer](https://marketplace.visualstudio.com/items?itemName=rust-lang.rust-analyzer)
