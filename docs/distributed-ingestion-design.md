# Distributed Ingestion Design

> **Status:** Design — shipped in phases 6–11. Drafted 2026-05-08 to lock the schema and
> wire protocol before implementation. All decisions in the "Decisions already locked"
> section of the brief were treated as inputs to this document.
>
> **Phase 12 update:** the desktop control surface moved off Tauri to a Kestrel-served
> React SPA at `src/aimemory.client/`. Every Tauri/`apps/desktop` reference below has a
> direct equivalent in the post-12 codebase — the API endpoints are unchanged, the admin
> calls go through `src/aimemory.client/src/api/admin.ts` instead of Rust `invoke` shims,
> and service control is exposed as `/api/admin/services/*` instead of Tauri commands.
> The schema, threat model, and wire protocol are untouched.

ai-memory currently runs as a single-machine bundle: `aimemory-api` and `aimemory-ingestor`
Windows Services on the same box, both writing to one SQLite DB. Distributed mode adds a
second deployment shape — a remote ingestor that forwards events over the network to a
primary's API — without changing the single-machine experience.

The same project (a git repo, typically) can exist on multiple machines. Cross-host content
must dedupe; per-host provenance must survive. The primary's DB is the source of truth.

---

## 1. Schema deltas

Three new tables, two altered. Naming follows existing `snake_case` columns + `lowercase_table` convention from `AIMemoryDbContext`. PKs are `Guid` (`TEXT` in SQLite) for parity with the rest of the codebase, except where a natural key reads better.

### 1.1 New table: `hosts`

The set of machines that have ever ingested into this primary. The local machine is row #1.

```sql
CREATE TABLE hosts (
  host_id          TEXT PRIMARY KEY,    -- lowercase hex sha-256, see §2.1
  friendly_name    TEXT NOT NULL,       -- user-facing label, e.g. "eric-laptop"
  os_kind          TEXT NOT NULL,       -- "windows" | "linux" | "macos"
  is_local         INTEGER NOT NULL,    -- 1 for the primary's own host, 0 for remotes
  first_seen_at    TEXT NOT NULL,
  last_seen_at     TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_hosts_friendly_name ON hosts(friendly_name);
```

`friendly_name` is unique to keep the pairing UI honest — two machines with the same display name will collide and force the user to disambiguate.

### 1.2 New table: `projects`

Cross-host project identity. One row per logical project regardless of how many hosts have a copy of it.

```sql
CREATE TABLE projects (
  project_id            TEXT PRIMARY KEY,    -- sha-256 hex, see §2.2
  display_name          TEXT NOT NULL,       -- nice label, can be edited
  canonical_remote_url  TEXT,                -- normalized, see §2.2
  root_commit_sha       TEXT,                -- 40-char lowercase hex, or NULL for non-git
  identity_kind         TEXT NOT NULL,       -- "git" | "fallback"
  first_seen_at         TEXT NOT NULL,
  last_seen_at          TEXT NOT NULL
);
CREATE INDEX ix_projects_canonical_remote_url ON projects(canonical_remote_url);
```

### 1.3 New table: `file_locations`

Per-host pointer to where a content blob lives. This is the join table that makes "same content on two machines" dedupe correctly while preserving "what does each host have on disk."

```sql
CREATE TABLE file_locations (
  host_id            TEXT NOT NULL REFERENCES hosts(host_id),
  project_id         TEXT NOT NULL REFERENCES projects(project_id),
  rel_path           TEXT NOT NULL,           -- POSIX-style, project-root relative
  content_sha256     TEXT NOT NULL,           -- joins to code_files.content_hash
  language           TEXT NOT NULL,
  file_size          INTEGER NOT NULL,
  first_seen_at      TEXT NOT NULL,
  last_seen_at       TEXT NOT NULL,
  PRIMARY KEY (host_id, project_id, rel_path)
);
CREATE INDEX ix_file_locations_content_sha256 ON file_locations(content_sha256);
CREATE INDEX ix_file_locations_project        ON file_locations(project_id);
```

The composite PK is the natural idempotency key for ingest (§3.4).

### 1.4 Altered: `code_files`

`code_files` becomes the content-addressed table. The old `(repository_id, file_path)` unique index is dropped; that uniqueness now lives in `file_locations`. Same content on two hosts produces one `code_files` row, two `file_locations` rows.

| Column | Before | After |
|---|---|---|
| `file_id` | PK Guid | **Deprecated** — kept for the migration window, dropped in a follow-up migration once symbols have been re-keyed. |
| `repository_id` | FK | **Removed** — repos are now `projects`. |
| `file_path` | indexed | **Removed** — moved to `file_locations.rel_path`. |
| `content_hash` | per-file | Renamed to `content_sha256`, becomes the **PK**. |
| `language` | per-file | Kept (denormalized; one content blob has one language). |
| `file_size` | per-file | Kept (same reasoning). |
| `indexed_at` | per-file | Renamed `first_seen_at`; new `last_seen_at`. |

Final shape:

```sql
CREATE TABLE code_files (
  content_sha256   TEXT PRIMARY KEY,    -- lowercase hex, no encoding wrapper
  language         TEXT NOT NULL,
  file_size        INTEGER NOT NULL,
  first_seen_at    TEXT NOT NULL,
  last_seen_at     TEXT NOT NULL
);
```

### 1.5 Altered: `code_symbols`

Symbols are owned by content, not by file path. Re-key from `file_id` to `content_sha256`.

| Column | Before | After |
|---|---|---|
| `file_id` (FK) | uuid | Replaced by `content_sha256` (string FK to `code_files`). |
| `repository_id` | uuid | Replaced by `project_id` (string FK to `projects`). |

Existing `symbol_key` format `filepath::QualifiedName#kind` becomes ambiguous across hosts. Resolve by re-keying as `project_id::QualifiedName#kind` — a symbol is a property of the project's content, not a host's working copy. The MCP tool callers do not see `symbol_key` directly; they pass it back as an opaque token, so the change is internal.

### 1.6 Altered: `code_repositories`

Becomes a thin compatibility shim for one migration cycle, then dropped. The `Name` field maps to `projects.display_name`. After the migration, the `code_repositories` table is removed entirely; existing API endpoints (`/api/code/repos`, etc.) keep their paths but query `projects` underneath.

### 1.7 New table: `pairings`

```sql
CREATE TABLE pairings (
  pairing_id        TEXT PRIMARY KEY,    -- Guid
  host_id           TEXT NOT NULL REFERENCES hosts(host_id),
  api_key_id        TEXT NOT NULL REFERENCES api_keys(api_key_id),
  friendly_name     TEXT NOT NULL,
  paired_at         TEXT NOT NULL,
  last_contact_at   TEXT,
  is_revoked        INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ux_pairings_host_id ON pairings(host_id) WHERE is_revoked = 0;
```

The partial unique index allows historical revoked pairings to coexist with one active pairing per host.

### 1.8 EF migration semantics

- One migration: `20260601_AddDistributedIdentity` (date is illustrative).
- Adds `hosts`, `projects`, `file_locations`, `pairings`.
- Backfills (§7) run **inside** the migration, in a `migrationBuilder.Sql(...)` block, using only the existing data — they don't touch the network.
- Drops `code_files.file_id` PK and old indexes; rebuilds the table with new shape via SQLite's "create new, copy, swap" idiom (EF emits this automatically when columns are removed under SQLite).
- `code_repositories` is **kept** through this migration. A follow-up migration in a later phase removes it once endpoints have moved.

`Database.Migrate()` runs at API startup (already wired in `Program.cs:210`); no change to the bootstrap path.

---

## 2. Identity derivation

### 2.1 `host_id`

A stable per-machine identifier. Survives reboots and OS upgrades. Changes if the user reimages.

**Windows:** `MachineGuid` from `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`. Set by Windows Setup, never rewritten by service packs or feature updates. A reimage produces a fresh GUID. Already used by other Windows tooling (e.g. ETW collectors) for exactly this purpose.

**Linux:** `/etc/machine-id` (systemd) or `/var/lib/dbus/machine-id` (older). Same stability properties.

**macOS:** `IOPlatformUUID` from `IOKit`. Same stability properties.

To prevent the raw machine GUID from leaking on the wire, hash it with a per-install salt:

```
host_id = sha256_hex( machine_guid_bytes || aimemory_install_salt )
```

`aimemory_install_salt` is a 32-byte random value generated at API first-run, persisted to `%ProgramData%\AIMemory\Api\install-salt.bin` (or platform equivalent) with file ACLs restricted to LocalSystem + Administrators. The salt makes `host_id` unique per AIMemory install on a machine, but stable across upgrades — the install-salt file survives MSI/NSIS upgrades because the postinstall hook leaves `%ProgramData%\AIMemory` alone (see `apps/desktop/src-tauri/installer/installer.nsh:69`).

Output is lowercase hex, 64 chars.

### 2.2 `project_id`

```
project_id = sha256_hex( root_commit_sha || "\n" || canonical_remote_url )
```

Both inputs are lowercase ASCII. The `\n` separator prevents `aabb` + `ccdd` from colliding with `aa` + `bbccdd`.

**`root_commit_sha`:** the SHA-1 of the commit with no parents (the initial commit). Discovered via `libgit2` walk: start at HEAD, walk first-parent chain to the bottom, then verify it has zero parents. The codebase already takes a libgit2 dependency (`LibGit2Sharp 0.31`, see `AIMemory.CodeIndex.Git.GitChangeDetector`).

**Shallow-clone caveat:** a shallow clone fabricates the bottom of its history by truncating parent links. The "root" we'd find is whatever commit is at the shallow boundary — different per clone depth. Detect via `git rev-parse --is-shallow-repository` (or `Repository.Info.IsShallow` in LibGit2Sharp). On a shallow repo, refuse to compute `project_id` — emit a warning, fall back to the non-git identity strategy below, and surface a UI hint suggesting `git fetch --unshallow`.

**`canonical_remote_url` normalization rules:**

1. Source: the URL of the `origin` remote, or — if missing — the first remote in alphabetical order. If no remotes, fall through to the non-git fallback.
2. Lowercase the entire string.
3. Strip credentials: drop any `user[:pass]@` between `://` and the host.
4. Strip trailing `.git`.
5. Strip trailing `/`.
6. Convert SSH form to HTTPS form: `git@github.com:owner/repo` and `ssh://git@github.com/owner/repo` both → `https://github.com/owner/repo`. (Match `^(?:ssh://)?git@([^:/]+)[:/](.+)$`.)
7. Default scheme: if no scheme remains after the SSH conversion, prepend `https://`.

Examples:

| Input | Canonical |
|---|---|
| `https://github.com/Eric/AIMemory.git` | `https://github.com/eric/aimemory` |
| `git@github.com:Eric/AIMemory.git` | `https://github.com/eric/aimemory` |
| `https://token@gitlab.com/me/proj/` | `https://gitlab.com/me/proj` |
| `ssh://git@bitbucket.org/team/repo` | `https://bitbucket.org/team/repo` |

**Non-git fallback** (`identity_kind = "fallback"`): `project_id = sha256_hex( "fallback\n" || host_id || "\n" || absolute_path )`. Including `host_id` in the input makes it explicit that this id will not match across hosts — the user accepts no cross-host dedupe for non-git projects.

### 2.3 `content_sha256`

SHA-256 of the file's bytes. Streaming: read in 64 KiB chunks into `IncrementalHash`, never load the whole file. Output: lowercase hex, 64 chars, no `0x`, no base64 wrapper. Matches the existing `CodeFile.ContentHash` format already produced by `AIMemory.CodeIndex.Files.ContentHasher`, so no transitional encoding step is needed.

---

## 3. Wire protocol

The primary's existing API gains three additions: a network bind toggle, the `/api/pairings` resource, and a back-compatible extension to `/api/ingest/batch`.

### 3.1 Header rename

The current middleware reads `X-API-Key` (`ApiKeyAuthMiddleware.cs:58`). The brief specifies `X-AIMemory-Api-Key`. Resolve by accepting **both** headers during the migration window, preferring the new one. Document both in the API surface, and have the desktop UI / installer wizards emit only the new one. The legacy header stays for at least one minor release to avoid breaking the MCP server's currently-shipped configs.

### 3.2 Pairings resource

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/pairings` | `admin` scope | Secondary registers itself. Body below. |
| `GET` | `/api/pairings` | `admin` scope | List all pairings (UI). |
| `DELETE` | `/api/pairings/{id}` | `admin` scope | Revoke a pairing. Sets `is_revoked = 1` and deactivates the linked api key. |

`POST /api/pairings` request:

```json
{
  "hostId": "f3a1...64hex",
  "friendlyName": "eric-laptop",
  "osKind": "windows",
  "ingestorVersion": "1.0.0"
}
```

Response (201):

```json
{
  "pairingId": "0a8f...guid",
  "hostId": "f3a1...64hex",
  "pairedAt": "2026-05-08T17:31:00Z"
}
```

The secondary authenticates this `POST` with an admin-scoped API key issued by the primary's UI before the wizard runs. After registration, the secondary is expected to use a downgraded `ingest`-scope key for actual ingestion (the primary's UI can issue both as a single bundle).

### 3.3 Bulk ingest extension

`POST /api/ingest/batch` already exists (`Program.cs:722`). Extend `BatchIngestRequest`:

```csharp
public class BatchIngestRequest
{
    public string ClientId   { get; set; } = "";    // existing
    public string MachineName{ get; set; } = "";    // existing — display only, do not trust
    public string Source     { get; set; } = "";    // existing
    public string? HostId    { get; set; }          // NEW — required for v2 secondaries
    public string? ProjectId { get; set; }          // NEW — derived per §2.2, batch-scoped
    public List<IngestEvent> Events { get; set; } = [];
}
```

`HostId` and `ProjectId` are **per-batch**, not per-event, because every adapter run scans one project on one host. The single-machine ingestor populates both with its local values. A v1 (no-HostId) batch is treated as `HostId = primary's local host_id` for backcompat — covers the upgrade case where the local ingestor hasn't been updated yet.

The two existing code events (`CodeFileUpsertEvent`, `CodeFileDeleteEvent`) gain a `RelPath` field that supersedes `FilePath`, normalized to forward slashes and project-root-relative. `FilePath` stays in the DTO for one release, populated by the secondary as a fallback for v1 primaries; new servers ignore it when `RelPath` is set.

### 3.4 Idempotency

The existing system already has an `IngestionLogEntry` table keyed by `IdempotencyKey` (`AIMemoryDbContext.cs:175`). Reuse it. Define the key as:

```
ik = sha256_hex( host_id || "|" || project_id || "|" || rel_path || "|" || content_sha256 || "|" || event_type )
```

This makes retries naturally idempotent: same host, same project, same path, same content, same event type → same key → ignored on second arrival (the existing `FilterExistingKeysAsync` path handles this with no logic change). Symbol-batch events use:

```
ik = sha256_hex( host_id || "|" || project_id || "|" || rel_path || "|" || content_sha256 || "|" || "CodeSymbolBatch" )
```

so a re-upload of the same symbols for the same content is also a no-op.

Idempotency is **not** an HTTP-level header; it's a property of the event payload. Cleaner for replay-after-network-failure scenarios because the secondary just sends the same batch again without tracking which requests succeeded.

### 3.5 Auth

`X-AIMemory-Api-Key` header (with backcompat per §3.1). Keys are stored in the existing `api_keys` table. The schema already holds salted hashes — `KeyHash` is `sha256_hex(raw_key)` (`Program.cs:1135`). That's actually a non-salted SHA-256, which is fine for these high-entropy 32-hex-char keys but should be flagged in §8 for future hardening.

### 3.6 TLS

Self-signed cert generated at first API run when "allow remote" is toggled on. Stored at `%ProgramData%\AIMemory\Api\tls.pfx`. Password persisted in `%ProgramData%\AIMemory\Api\tls.pwd` with restricted ACLs.

- **Cert subject:** `CN=aimemory-primary`, SANs include `localhost`, `127.0.0.1`, `::1`, the machine's hostname, and any non-link-local IPv4/IPv6 the user selected as a bind interface.
- **Validity:** 825 days (Apple's max). Auto-rotated 60 days before expiry — emits a UI warning, prompts user to re-pair.
- **Fingerprint:** `sha256_hex` of the DER bytes of the leaf cert. Displayed in primary UI as colon-separated uppercase pairs (`A1:B2:...`) for readability; transmitted on the wire as plain lowercase hex.
- **Validation on the secondary:** TOFU. The wizard pins the fingerprint at pairing time; subsequent connections that present a different leaf are refused with a hard error and a UI surface telling the user to re-pair.

The bind interface is configurable in `appsettings.json` under `AIMemory:BindAddress`. Default `127.0.0.1` (current behavior). When the user toggles "Allow remote ingestors" in the desktop UI, the value is rewritten to `0.0.0.0` and the API restarts via the Tauri service control layer.

---

## 4. Pairing flow

### 4.1 Primary side — turning on remote access

1. User opens AIMemory Desktop → Settings → Distributed → toggles **Allow remote ingestors**.
2. Tauri shell calls a new `/api/admin/distributed/enable` endpoint. The handler:
   - Generates `tls.pfx` if missing.
   - Computes the fingerprint.
   - Rewrites `appsettings.json` to `BindAddress: 0.0.0.0` and switches the URL to `https://`.
   - Issues a fresh API key with `ingest` + an internal `pair` scope (one-time admin equivalent that auto-expires after 24h).
   - Restarts `aimemory-api` via the Windows SCM (`apps/desktop/src-tauri/src/services.rs` already controls service lifecycle).
3. UI displays:
   - The endpoint URL (`https://<lan-ip-or-hostname>:<port>`)
   - The API key (one-time reveal, copy-to-clipboard, hidden after the user dismisses)
   - The cert fingerprint (`SHA-256: A1:B2:C3:...`)
4. UI also shows a list of currently paired hosts with revoke buttons (queries `GET /api/pairings`).

### 4.2 Secondary side — running the ingestor-only installer

1. User runs the same NSIS installer, picks **Ingestor-only (Remote node)** on the Components page. Installer registers only `aimemory-ingestor` as a service, skips the API service and the desktop shell binaries. Shared MSI, smaller install set.
2. Postinstall, the Tauri **Setup wizard** (a stripped-down second window the desktop bundle gains; runs once on first launch when no API is local) opens. (Note: the ingestor-only install ships the desktop UI's wizard binary but not the full desktop control panel — this is a follow-up packaging detail to confirm in phase 6.) The wizard collects:
   - Endpoint URL (e.g. `https://eric-desktop.lan:5219`)
   - API key (paste from primary)
   - Cert fingerprint (paste from primary)
   - Friendly name (defaults to `Environment.MachineName`)
3. Wizard performs an HTTPS handshake against `GET /api/health`. Compares the leaf cert's SHA-256 fingerprint to the pasted value; mismatch aborts the wizard with a clear error.
4. Wizard `POST /api/pairings` with the secondary's `host_id`, `friendlyName`, `osKind`, `ingestorVersion`.
5. Primary persists the pairing, returns the `pairingId`.
6. Wizard writes `%ProgramData%\AIMemory\Ingestor\appsettings.json`:
   ```json
   {
     "Ingestor": {
       "AIMemoryApiBaseUrl": "https://eric-desktop.lan:5219",
       "ApiKey": "aimemory_...",
       "PinnedCertFingerprint": "a1b2..."
     }
   }
   ```
7. Wizard starts `aimemory-ingestor`. Done.

### 4.3 Revocation

User clicks revoke on a pairing in the primary's UI:

1. Primary sets `pairings.is_revoked = 1` and `api_keys.is_active = 0` for the linked key.
2. Existing api-key cache entry expires after 60s (`ApiKeyAuthMiddleware.cs:15`); subsequent requests from that secondary hit the DB and 401.
3. Secondary's `RemoteSink` sees a 401, surfaces a clear error in its log + a tray notification (if the desktop wizard is installed), pauses ingestion, and stops retrying. User must re-pair.

---

## 5. Threat model

LAN-only is the design target. WAN is explicitly out of scope (see §9).

| Threat | Mitigation |
|---|---|
| **LAN passive sniffing** | TLS 1.2+, fingerprint-pinned by the secondary (TOFU). |
| **LAN MITM** | Same — pinned fingerprint defeats a re-signed cert. |
| **Replay** | TLS protects the wire; idempotency keys (§3.4) make application-level replay a no-op. |
| **API key leak** | Key is hashed in the DB (sha-256, single round). UI exposes raw key once at issue, never again. Revocable per-pairing. |
| **Compromised secondary** | Can poison its own data — write false content under its `host_id`. **Not mitigated in v1.** Trust is established at pairing time and never re-verified. The user controls the pairing list. Operators who need higher assurance should not pair untrusted hosts. |
| **Cross-host write isolation** | Not enforced. A secondary's API key is scoped to `ingest`, but `ingest` doesn't restrict which `(host_id, project_id)` combinations the secondary can write. v1 is honor-system. |
| **WAN exposure** | API binds to user-specified interface; default is loopback. The "Allow remote" toggle is explicit. UI warns the user that exposing the bind to a public IP without a VPN is unsafe. |

---

## 6. Failure modes

| Failure | Behavior |
|---|---|
| Network down (secondary → primary) | Ingestor's existing `OutboxStore` (`Transport/OutboxStore.cs`) catches the failed batch; retried each cycle by `RetryOutboxAsync` (`Worker.cs:230`). Backoff: existing scan interval (5s default). 100 MB outbox cap; oldest-first purge above. |
| API key revoked | Secondary gets 401, stops ingesting, surfaces error in log + (if desktop installed) tray. No automatic re-pair. |
| Cert rotated on primary | Pinned fingerprint mismatches, secondary refuses connection with a hard error. User re-pairs via the wizard. |
| Disk full on primary | Primary returns HTTP 507 from `/api/ingest/batch`. Secondary treats 5xx as transient, queues to outbox, backs off. |
| Concurrent pairings, same `host_id` | Primary rejects the second `POST /api/pairings` with HTTP 409 + body explaining the active pairing must be revoked first. User resolves via the primary's UI. |
| Project signature changes (e.g. repo's first commit got rewritten by a force-pushed `git filter-repo`) | New `project_id`. Old project becomes orphaned — its `file_locations` rows still reference an existing `project_id` that's no longer being updated. UI must surface "stale projects" so the user can prune. **Documented v1 risk; no automatic merge.** |
| Two hosts with the same `friendly_name` | Pairing rejected with 409. Secondary wizard prompts user to pick a different name. |
| Network partition mid-batch | Primary's batch handler is per-event transactional; partial application is fine because idempotency keys make the retry a no-op for already-applied events. |
| Clock skew between hosts | Timestamps are display-only, not used for ordering. `last_seen_at` may be slightly stale — acceptable. |
| Secondary running ingestor older than primary's wire version | Backcompat: primary accepts v1 batches (no `HostId`/`ProjectId`) and treats them as local. v2 primary + v1 secondary works on the local box; cross-host requires both at v2. |

---

## 7. Backfill plan

Run once, in the migration that adds the new tables.

1. **Synthesize local `host_id`.** Read or create `install-salt.bin`. Compute `host_id` per §2.1. Insert `hosts` row with `is_local = 1`, `friendly_name = Environment.MachineName` (de-collide with `-2`, `-3` suffixes if the chosen name already exists).
2. **Walk existing `code_repositories`.** For each row:
   - Open the source path with libgit2. If it's a non-shallow git repo with a remote, derive `project_id` per §2.2 with `identity_kind = "git"`.
   - Otherwise, derive a fallback id: `sha256_hex("fallback\n" || host_id || "\n" || source_path)` with `identity_kind = "fallback"`.
   - Insert into `projects`. `display_name = code_repositories.name`.
3. **Walk existing `code_files`.** For each row:
   - Insert (or upsert) into the new `code_files` table keyed by `content_sha256`. Use the existing `content_hash` value verbatim.
   - Insert a `file_locations` row with `host_id = local`, `project_id = mapped`, `rel_path = file_path` (already project-root-relative in current data), `content_sha256` from the same row, `first_seen_at = last_seen_at = code_files.indexed_at`.
4. **Re-key existing `code_symbols`.** Update `symbol_key` from `filepath::QualifiedName#kind` to `project_id::QualifiedName#kind`. Update `repository_id` references to `project_id`. Update `file_id` references to `content_sha256`.
5. **Drop the old `code_files.file_id` column** at the end of the same migration.
6. **`code_repositories` is left in place** for one release as a read-only compatibility view (or stays as a real table that gets populated alongside `projects` — the API layer reads from `projects` regardless). Removed in a follow-up migration.

Backfill is one-way. The migration is non-reversible (`Down` throws). This is consistent with existing AIMemory migrations (no real reversibility expected for SQLite production data).

---

## 8. Open questions / deferred to v2

- **API key rotation UX.** Currently keys are forever. Need a UI flow for "issue new, deprecate old, give grace period."
- **Multi-key per pairing.** A single secondary uses one key. If we ever want per-source granularity (e.g. "this ingestor can write code events but not session events"), we need a key-set per pairing.
- **Stronger scope checks.** Today an `ingest`-scoped key from secondary A could in principle write events claiming `host_id` of secondary B. Not a real attack vector on a LAN-of-trust, but worth tightening: bind `host_id` into the key's metadata and reject mismatches.
- **Project rename detection.** Renaming a git remote (e.g. moving from GitHub to GitLab) changes the canonical URL → new `project_id`. No automatic merge in v1; the user has to manually unify projects via a future "merge projects" UI.
- **Branch as part of project identity.** Currently a single project_id covers all branches. If users want per-branch indexing, we'd want a composite `(project_id, branch)` key on file_locations. Deferred.
- **Salted API key hashes.** `KeyHash` is a single SHA-256 round. Fine for 128-bit-entropy keys but worth moving to a salted KDF (Argon2id) to be defense-in-depth.
- **Shallow clone heuristic.** We refuse to compute `project_id` on shallow repos. A better answer might be to record the shallow boundary as a tagged variant. Deferred.
- **Cert auto-rotation flow.** Documented as a manual re-pair today. Could be automated by signing a rotation event with the old key.
- **Secondary self-update.** The installer ships an ingestor binary version. Out-of-band upgrades are manual.

---

## 9. Non-goals

The following are explicitly **not** part of this design and will not be added without a separate design pass:

- Push to multiple primaries from one secondary.
- Peer-to-peer ingestion between secondaries.
- Encrypted-at-rest content blobs (the DB is plaintext SQLite; OS-level disk encryption is the user's responsibility).
- Audit log of who-changed-what beyond the existing `IngestionLogEntry`.
- WAN-safe deployment without user-provided VPN/tunnel.
- Conflict resolution for two hosts editing the same `(project_id, rel_path)` to different content. Both are stored, both are queryable, the UI shows both. There is no "primary wins" or "latest wins" rule.
- Selective sync (e.g. "this host only syncs files matching `**/*.py`"). All discoveries by the secondary are forwarded.

---

## 10. Surfaces touched by this change

Implementation checklist for phase 6+. Each item is a discrete unit of work; concerns are
factored so different phases can take adjacent slices.

**Storage / data model**
- `src/AIMemory.Models/Entities/Host.cs` — new
- `src/AIMemory.Models/Entities/Project.cs` — new
- `src/AIMemory.Models/Entities/FileLocation.cs` — new
- `src/AIMemory.Models/Entities/Pairing.cs` — new
- `src/AIMemory.Models/Entities/CodeFile.cs` — restructure to content-addressed
- `src/AIMemory.Models/Entities/CodeSymbol.cs` — re-key from file_id to content_sha256
- `src/AIMemory.Data/AIMemoryDbContext.cs` — add `DbSet`s and entity configs for new tables; alter existing
- `src/AIMemory.Data/Migrations/2026xxxx_AddDistributedIdentity.cs` — new migration with backfill SQL
- `src/AIMemory.Data/Repositories/IHostRepository.cs` + impl — new
- `src/AIMemory.Data/Repositories/IProjectRepository.cs` + impl — new
- `src/AIMemory.Data/Repositories/IPairingRepository.cs` + impl — new
- `src/AIMemory.Data/Repositories/CodeIndexRepository.cs` — rework to operate on projects + file_locations
- `src/AIMemory.Data/Repositories/SqliteSearchRepository.cs` — symbol queries gain a host filter

**API / wire**
- `src/AIMemory.Api/Program.cs` — bind interface from config; HTTPS pipeline; new pairing endpoints; `/api/admin/distributed/enable`; ingest batch handler honors `HostId`/`ProjectId`
- `src/AIMemory.Api/Middleware/ApiKeyAuthMiddleware.cs` — accept `X-AIMemory-Api-Key` (preferred) and `X-API-Key` (legacy)
- `src/AIMemory.Api/Tls/TlsCertificateProvider.cs` — new, generates and persists self-signed cert
- `src/AIMemory.Api/Tls/CertFingerprint.cs` — new, computes SHA-256 of DER
- `src/AIMemory.Api/appsettings.json` — `BindAddress`, `Tls` section
- `src/AIMemory.Models/Dtos/BatchIngestRequest.cs` — `HostId`, `ProjectId` fields
- `src/AIMemory.Models/Dtos/PairingDtos.cs` — new
- `src/AIMemory.Models/Events/CodeFileUpsertEvent.cs` — `RelPath` field
- `src/AIMemory.Models/Events/CodeFileDeleteEvent.cs` — `RelPath` field

**Identity layer (new project)**
- `src/AIMemory.Identity/HostIdProvider.cs` — reads machine guid + salt
- `src/AIMemory.Identity/ProjectIdResolver.cs` — git-aware resolution + canonical URL normalization
- `src/AIMemory.Identity/InstallSaltStore.cs` — read/write `install-salt.bin`
- Used by both API (for backfill, local host registration) and Ingestor (per-batch project resolution)

**Ingestor**
- `src/AIMemory.Ingestor/Configuration/IngestorConfig.cs` — `PinnedCertFingerprint` field
- `src/AIMemory.Ingestor/Transport/ILedgerSink.cs` — new abstraction
- `src/AIMemory.Ingestor/Transport/LocalSink.cs` — wraps current HTTP path
- `src/AIMemory.Ingestor/Transport/RemoteSink.cs` — HTTPS + fingerprint-pinning `HttpClientHandler.ServerCertificateCustomValidationCallback`
- `src/AIMemory.Ingestor/Transport/AIMemoryHttpClient.cs` — pulls `HostId`, `ProjectId` from new `IIngestorContext`
- `src/AIMemory.Ingestor/Adapters/CodeAdapter.cs` — emits `RelPath`, includes `ProjectId` per scan
- `src/AIMemory.Ingestor/Worker.cs` — populate `BatchIngestRequest.HostId/ProjectId`

**Desktop (Tauri)**
- `apps/desktop/src-tauri/src/runtime.rs` — runtime.json gains `tls_fingerprint`, `bind_address`
- `apps/desktop/src-tauri/src/distributed.rs` — new, Tauri commands for enable/disable, list/revoke pairings, generate keys
- `apps/desktop/src-tauri/src/services.rs` — restart on bind change
- `apps/desktop/src/pages/Settings/Distributed.tsx` — new page with toggle, key/fingerprint reveal, pairings table
- `apps/desktop/src/pages/Setup/RemoteWizard.tsx` — new, runs on first launch when no local API + saves to ingestor appsettings.json

**Installer**
- `apps/desktop/src-tauri/installer/installer.nsh` — Components page (Full / Ingestor-only); conditional service registration; conditional binary copy
- NSIS components definition in `tauri.conf.json` (or `bundle.nsis.installerHooks`)
- Drop `src/AIMemory.Ingestor.Installer` (WiX project) — superseded by the unified NSIS installer; or keep as a separate enterprise option

**Tests**
- `tests/AIMemory.Tests.Unit/Identity/ProjectIdResolverTests.cs` — URL normalization, root commit walk, shallow detection
- `tests/AIMemory.Tests.Unit/Identity/HostIdProviderTests.cs` — salt + hash determinism
- `tests/AIMemory.Tests.Unit/Api/PairingEndpointTests.cs` — register/list/revoke happy paths + 409 on duplicate
- `tests/AIMemory.Tests.Unit/Api/CertFingerprintTests.cs`
- `tests/AIMemory.Tests.Unit/Ingestor/RemoteSinkTests.cs` — fingerprint pinning, mismatch rejection
- `tests/AIMemory.Tests.Unit/Data/MigrationBackfillTests.cs` — given v1 fixture, after migrate the new tables hold expected rows
- `tests/AIMemory.Tests.Integration/DistributedSmokeTests.cs` — two-process live-fire (primary + remote ingestor on loopback)

**Documentation**
- `README.md` — add "Distributed mode" section after "Architecture"
- `docs/distributed-ingestion-design.md` — this file
- `apps/desktop/README.md` — replace placeholder with real overview, mention pairing
