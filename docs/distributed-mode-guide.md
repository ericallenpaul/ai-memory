# Distributed mode guide

ai-memory's default deployment is single-machine: the desktop shell, the API
service, and the ingestor all run on one box, writing to a single SQLite
database. Distributed mode adds a second deployment shape — a remote machine
that runs only the ingestor and forwards code-index events over the LAN to a
primary AIMemory server.

This guide is the end-to-end how-to. For the architectural rationale, schema
deltas, and threat model, see
[`distributed-ingestion-design.md`](./distributed-ingestion-design.md).

> **Status:** Shipped in phases 5–11 (commits `23cced8`..`aa7eb70`). LAN-only.
> WAN deployment is not supported in v1; if you need that, terminate a VPN
> first.

---

## When to use distributed mode

Use distributed mode when you have more than one development machine and want
a single AIMemory database to be the source of truth across all of them.
Typical setups:

- A desktop and a laptop, both with checkouts of the same repos. You want the
  desktop's index to also see the laptop's changes when you switch machines.
- A workstation plus a build server. The build server has a different layout
  on disk but identical content; you want one logical project, not two.
- Multiple developers — each contributes ingest from their own machine into a
  shared on-prem AIMemory primary. (Per-host write isolation is honor-system
  in v1; see [Security model](#security-model).)

If you only ever use one machine, you don't need this. Stick with the default
full install.

---

## Two install modes

The NSIS installer (`AIMemory Desktop_<ver>_x64-setup.exe`) opens with a
"Setup Type" page and offers two choices:

- **Full install** *(default)* — installs the desktop dashboard plus both
  Windows services (`aimemory-api`, `aimemory-ingestor`). This is the existing
  single-machine experience. The primary in a distributed setup is a full
  install with remote ingestors enabled.
- **Ingestor-only (Remote node)** — installs only `aimemory-ingestor` plus a
  one-time pairing wizard. There is no local API and no dashboard. The
  ingestor on this machine forwards events to a remote primary over HTTPS.

The same installer ships both modes; the choice is persisted in
`HKLM\Software\AIMemory\InstallMode` and a marker file at
`%ProgramData%\AIMemory\app-mode.json`. The Tauri shell reads the marker on
launch and renders the dashboard or the wizard accordingly.

Switching modes after install is not supported in v1 — uninstall and reinstall
with the other choice.

---

## End-to-end walkthrough

The flow is: enable on the primary, install the secondary, paste three values
into the wizard, pair.

### 1. Enable remote ingestors on the primary

On the machine that will hold the canonical database:

1. Make sure you have a working full install. The desktop should open, and
   `Services` should show `aimemory-api` and `aimemory-ingestor` running.
2. Open the desktop, navigate to the **Distributed** page in the sidebar.
3. Click **Allow remote ingestors**. A confirm dialog appears with a bind
   interface picker.
4. Pick the LAN interface you want the API to listen on:
   - The dropdown is populated by Rust's network-interface enumerator.
     Pick the entry whose IP is reachable from the secondary.
   - `Any (0.0.0.0)` binds to all interfaces. Use this if the machine has
     more than one network and you want both reachable.
   - **Custom** lets you type an IPv4 explicitly. Loopback addresses
     (`127.0.0.1`, `localhost`) are accepted but will log a warning — they
     defeat the point of distributed mode.
5. Click **Confirm**. The API:
   - Generates a self-signed TLS certificate at
     `%ProgramData%\AIMemory\Api\tls.pfx` (825-day validity).
   - Computes the SHA-256 fingerprint of its DER bytes.
   - Issues a fresh ingest-scoped API key.
   - Persists the new state to
     `%ProgramData%\AIMemory\Api\distributed.json`.
6. The page now shows a **one-time reveal** panel with three values:
   - `Endpoint` — e.g. `https://192.168.1.50:5219`.
   - `API key` — a string starting with `aimemory_`, formatted as 32 hex
     characters. Click the copy icon to grab it.
   - `Cert fingerprint` — formatted in `AA:BB:CC:...` style for readability.
7. Click **Restart API service** when prompted. The bind interface change
   needs a service restart to take effect; the desktop's existing service
   control (`services.rs`) handles this through the Windows SCM.

The reveal panel disappears once you dismiss it. The API key is stored as a
SHA-256 hash on disk — there is no way to view it again. If you lose it,
disable and re-enable distributed mode to issue a new key, then re-pair the
secondary.

### 2. Install ingestor-only mode on the secondary

On the machine that will push events:

1. Run the same installer (`AIMemory Desktop_<ver>_x64-setup.exe`).
2. On the **Setup Type** page, pick **Ingestor-only (Remote node)**.
3. Continue through the wizard. The installer:
   - Registers `aimemory-ingestor` as a Windows Service with start type
     Automatic. **It does not start the service yet** — pairing has to come
     first.
   - Skips registering `aimemory-api` and prunes `AIMemory.Api.*` from
     `$INSTDIR\resources\api\`.
   - Writes `%ProgramData%\AIMemory\app-mode.json` with `{"mode":
     "ingestor-only"}`.
   - Replaces the Start Menu shortcut with **AIMemory Ingestor Setup**.
4. Open **AIMemory Ingestor Setup** from the Start Menu (or wait for it to
   open at the end of install).

The Tauri shell reads `app-mode.json` and renders the wizard, not the full
dashboard.

### 3. Pair with the primary

1. The wizard pre-populates the **Host ID** field by shelling out to
   `AIMemory.Ingestor.exe --print-host-id`. This is the SHA-256 of the
   machine GUID plus the install salt — stable across reboots, changes on
   reimage. You can't edit it.
2. Paste the three values from the primary's Distributed page:
   - **Endpoint** — the `https://<ip>:<port>` URL.
   - **API key** — the `aimemory_...` string. The field hides input by
     default; toggle visibility if you want to verify before pasting.
   - **Cert fingerprint** — paste either the colon-separated form or plain
     hex. The wizard normalizes both.
3. Optionally set a **Friendly name**. Defaults to the machine's hostname.
   Must be unique across the primary's pairing list (collisions are
   rejected with HTTP 409).
4. Click **Test connection**. The wizard:
   - Opens a raw rustls TLS connection to the endpoint, captures the leaf
     cert, and computes its SHA-256. If that doesn't match the pasted
     fingerprint, the connection aborts before any HTTP request goes out.
   - Sends `GET /api/health` with the API key in the
     `X-AIMemory-Api-Key` header. A 200 means TLS pin and auth both passed.
5. Click **Pair**. The wizard runs the same handshake again, then sends
   `POST /api/pairings` with the host ID, friendly name, OS kind, and
   ingestor version. On success the primary returns a `pairingId`.
6. The wizard writes
   `%ProgramData%\AIMemory\Ingestor\appsettings.json`:

   ```json
   {
     "Ingestor": {
       "Mode": "Remote",
       "Remote": {
         "Endpoint": "https://192.168.1.50:5219",
         "ApiKey": "aimemory_...",
         "PinnedCertFingerprint": "a1b2c3..."
       }
     }
   }
   ```

7. The wizard navigates to the status page. Start the ingestor service
   (`sc.exe start aimemory-ingestor` from an elevated prompt, or use the
   **Start service** button on the status page). On startup, the ingestor's
   `IngestorConfigValidator` checks all three Remote fields; any missing or
   malformed value fails fast with a clear log message.

### 4. Verify ingestion is flowing

Back on the primary's **Distributed** page, the **Paired hosts** table now
lists the secondary. The columns are:

| Column | Source |
|---|---|
| `Friendly name` | What the secondary picked in the wizard. |
| `Host ID` | First 12 hex chars; full value on hover. |
| `Paired at` | Timestamp the primary recorded the pairing. |
| `Last contact` | Most recent successful authenticated request from this host. |

The table refreshes every 7 seconds. `Last contact` ticks forward each time
the secondary's `RemoteSink` posts a batch — that's the live signal that
ingestion is working.

To confirm content is actually being recorded, open the primary's **Database
browser** (Settings → Database) and run a query against the `file_locations`
table. Rows tagged with the secondary's `host_id` mean events are landing.
For more direct evidence, look at a project both machines have a copy of: you
should see one row in `projects`, one row per unique blob in `code_files`,
and two rows in `file_locations` (one per host) for any file with identical
content on both machines.

---

## How dedup works

The primary's database is content-addressed. Three concepts:

- **Project identity.** Computed as
  `sha256(root_commit_sha || "\n" || canonical_remote_url)`. Same git repo on
  two machines ⇒ same `project_id` ⇒ one row in `projects`. Non-git folders
  fall back to a per-host id (no cross-host dedup; see the design doc §2.2).
- **Content identity.** Each blob is keyed by SHA-256 of its bytes. Identical
  content on two machines ⇒ one row in `code_files`.
- **Per-host location.** `file_locations` joins `(host_id, project_id,
  rel_path) → content_sha256`. Same content on two machines = two
  `file_locations` rows pointing at one `code_files` row.

If the same file has different content on each machine — a stash on one box,
a clean checkout on the other — both blobs are kept. Each `file_locations`
row points at its host's version. There is no "primary wins" or "latest
wins" rule; both are queryable, both show up in symbol search results.

Renaming the git remote (e.g. moving a repo from GitHub to GitLab) changes
`canonical_remote_url`, which changes `project_id`. The old project goes
stale and the new one starts fresh. Manual project merge is not in v1.

---

## Security model

LAN-of-trust. Wire is TLS 1.2+ with a fingerprint pinned by the secondary at
pairing time (TOFU). Auth is the `X-AIMemory-Api-Key` header on every
request — the legacy `X-API-Key` was hard-cut in phase 7a. Loopback requests
bypass auth so the local ingestor on a primary keeps working. Revocation
flags the pairing row, deactivates its API key, and is enforced by the
ingest-batch handler on every batch — a revoked host gets HTTP 403 even
inside the 60-second key cache.

What's not protected: a compromised secondary can post false content under
its own `host_id`. Cross-host write isolation is honor-system in v1 — pair
only machines you control.

Full threat table and deferred items are in the design doc §5 and §8.

---

## Common issues

### "Test connection" reports the fingerprint doesn't match

The wizard captured a leaf cert whose SHA-256 doesn't match what you pasted.
Causes, in rough order of likelihood:

- You copied the wrong line from the primary, or pasted with extra
  whitespace. The wizard accepts colon-separated and plain hex, but it
  hashes the actual cert bytes — typo wins.
- The primary regenerated its cert (rare on a fresh install; happens if
  someone deleted `tls.pfx` on the primary). Re-copy the fingerprint from
  the primary's Distributed page and try again.
- Genuine MITM. Unusual on a LAN; if you're sure neither of the above
  applies, stop and investigate the network.

### "Test connection" reports unreachable

`PairError::Network` from rustls or reqwest. Things to check:

- The primary's Distributed page is enabled (the toggle says "Allow remote
  ingestors").
- `aimemory-api` is running on the primary (`sc.exe query aimemory-api`).
- The primary's host firewall allows inbound on the API port (5219 by
  default). On Windows, check `wf.msc` or the Windows Defender Firewall
  panel.
- The endpoint is the right IP. If you used `Any (0.0.0.0)` on the primary,
  any of its IPs should work; if you specified a single interface, that's
  the only IP that will accept connections.

### "Test connection" reports unauthorized

The TLS pin passed but the API key was rejected. Things to check:

- Whitespace. Re-copy from the primary's reveal panel.
- The key wasn't revoked since reveal. Check the **Paired hosts** list for
  any earlier pairings using the same key.
- If you can't recover the key, click **Disable** then **Allow remote
  ingestors** again on the primary. That issues a new key. Re-pair the
  secondary with the new value.

### Ingest is rejected with HTTP 403 after a successful pair

The host was revoked on the primary — the ingest-batch handler now rejects
batches whose `HostId` isn't in the active hosts table, even before the
api-key cache expires. Re-pair from the secondary's status page using the
**Re-pair** button.

### The built `setup.exe` is empty or missing the API binary

Phase 11's bundle layout copies the .NET binaries into
`apps/desktop/src-tauri/bundle-resources/{api,ingestor}/` before `tauri
build` runs. The `prepublish:dotnet` script does that publish; `tauri.conf.json`
chains it via `beforeBuildCommand`. If the chain failed silently and you got
a small (~3 MB) installer, run the publish manually and check the output:

```powershell
cd apps/desktop
npm run prepublish:dotnet
```

The expected output is two trees under `src-tauri/bundle-resources/`:
- `api/AIMemory.Api.exe` (~32 MB framework-dependent single-file)
- `ingestor/AIMemory.Ingestor.exe` (~33 MB)

Both directories also contain `appsettings.json`, `nlog.config`,
`web.config`, `wwwroot/`, `e_sqlite3.dll`, and `git2-*.dll`. After a clean
publish, re-run `npm run tauri build` (under vcvars64 — see the desktop
README).

### The wizard reports "could not read host ID"

The wizard shells out to `AIMemory.Ingestor.exe --print-host-id`. If the
ingestor binary isn't in the install dir (e.g. mid-install, or a partially
failed install), the flag won't resolve. Re-run the installer and pick
**Repair**.

---

## Limitations / not supported in v1

The following are deferred to a future release. Pulled from the design doc
§8:

- **Cert auto-rotation.** Manual today: delete `tls.pfx` on the primary,
  toggle distributed mode off and back on, re-pair every secondary.
- **API key rotation.** Same — keys are forever once issued. To rotate,
  revoke the old pairing on the primary, re-issue, and re-pair the secondary.
- **Multi-key per pairing.** A secondary uses one key for everything. There
  is no "this key can write code events but not session events" granularity.
- **Branch as part of project identity.** A single `project_id` covers all
  branches. Per-branch indexing would need a composite key on
  `file_locations` — not in v1.
- **Audit log.** The `IngestionLogEntry` table records what was applied, but
  there is no separate "who pushed what when" surface beyond `last_contact_at`
  on the pairings row.
- **Selective sync.** All discoveries by the secondary are forwarded. There
  is no "this host only syncs files matching `**/*.py`" filter.
- **Multi-primary push.** A secondary forwards to one primary. Pushing to
  two simultaneously is out of scope.
- **WAN-safe deployment.** No NAT traversal, no relay. Use a VPN.

---

## Reference

Files written by distributed mode (Windows):

- `%ProgramData%\AIMemory\app-mode.json` — selects dashboard vs ingestor
  wizard at launch (set by the installer).
- `%ProgramData%\AIMemory\Api\tls.pfx` + `tls.pwd` — self-signed cert and its
  password, generated on first enable.
- `%ProgramData%\AIMemory\Api\distributed.json` — persisted bind address,
  port, and fingerprint across service restarts.
- `%ProgramData%\AIMemory\Api\install-salt.bin` — per-install salt for
  `host_id` derivation.
- `%ProgramData%\AIMemory\Ingestor\appsettings.json` — ingestor mode +
  remote endpoint / API key / fingerprint (written by the wizard).

Endpoints (admin scope unless noted):

- `POST /api/admin/distributed/enable?bindInterface=<ip>` — generate cert,
  issue ingest key, set bind. `bindInterface` accepts an IPv4 string,
  `0.0.0.0`, or loopback (warning logged). Invalid input → 400.
- `POST /api/admin/distributed/disable` — restore `127.0.0.1`. Keeps the
  cached fingerprint so re-enable doesn't force re-pair.
- `GET /api/admin/distributed/status` — current bind, port, fingerprint,
  paired-host count.
- `POST /api/pairings` — secondary self-registration.
- `GET /api/pairings` / `DELETE /api/pairings/{id}` — list / revoke.
- `POST /api/ingest/batch` — gains optional per-batch `HostId` and
  `ProjectId`. Unknown `HostId` → 403.

For schema and wire-protocol details, see
[`distributed-ingestion-design.md`](./distributed-ingestion-design.md).
