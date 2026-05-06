# Plan: Code Ingestor Adapter + API Key Management + Machine Tracking

**Date:** 2026-03-10
**Branch:** main
**Status:** EXECUTED
**Supersedes:** main-2026-03-06-code-adapter-and-api-keys.md

---

## Goal

Three deliverables:

1. **Code Adapter** — A new `ISourceAdapter` in `AIMemory.Ingestor` that runs on the same schedule as log adapters, uses intelligent change tracking (mtime + content hash in JSON checkpoint) to only re-process changed files, and pushes code events through the existing batch pipeline. No one-time ingest mode — always scheduled, always incremental.

2. **Machine Name Tracking** — Both log and code ingestors record which machine they're running on. This flows into the database so you can see which machine produced each session/event.

3. **API Key Management** — DB-backed multi-key system with scopes, managed via the web UI.

---

## Part 1: Machine Name Tracking

### Problem

`BatchIngestRequest.ClientId` is set to a manual string (`"eric-windows-01"`) and only logged — never stored in the database. We need machine identity persisted with ingested data.

### Step 1: Add `MachineName` to `BatchIngestRequest`

```csharp
public class BatchIngestRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;  // NEW
    public string Source { get; set; } = string.Empty;
    public List<IngestEvent> Events { get; set; } = [];
}
```

### Step 2: Auto-populate `MachineName` in the Ingestor Worker

In `Worker.cs`, when building the `BatchIngestRequest`:
```csharp
var request = new BatchIngestRequest
{
    ClientId = _config.ClientId,
    MachineName = Environment.MachineName,  // auto-detected
    Source = adapter.SourceName,
    Events = batch.ToList()
};
```

Also default `ClientId` to `Environment.MachineName` if not explicitly configured:
```csharp
// IngestorConfig.cs
public string ClientId { get; set; } = Environment.MachineName;
```

### Step 3: Store machine name on `Session` and `IngestionLogEntry`

Add `MachineName` column to both entities:

**`Session.cs`** — add:
```csharp
public string? MachineName { get; set; }
```

**`IngestionLogEntry.cs`** — add:
```csharp
public string? MachineName { get; set; }
```

### Step 4: Propagate in API batch ingest handler

In the `/api/ingest/batch` endpoint:
- Pass `request.MachineName` into `SessionUpsertEvent` handling → set `session.MachineName`
- Pass `request.MachineName` into `IngestionLogEntry` creation → set `entry.MachineName`

### Step 5: DbContext + Migration

- Add `machine_name` column to `sessions` and `ingestion_log` tables
- Add index on `Session.MachineName`
- Migration: `AddMachineName`

---

## Part 2: Code Adapter with Intelligent Change Tracking

### Design: No One-Time Mode, Minimize Chatter

The code adapter runs on the same polling interval as log adapters (configurable, default 5s). On each cycle it:

1. Walks configured paths, filters to parseable files
2. For each file, checks the JSON checkpoint:
   - **mtime unchanged** → skip entirely (zero I/O beyond stat)
   - **mtime changed** → compute SHA256 content hash
     - **hash unchanged** (file was touched but content identical) → update checkpoint mtime, skip
     - **hash changed** → parse symbols, send events, update checkpoint with new hash + mtime
3. Only files with actual content changes generate API traffic

The checkpoint JSON file stores per-file:
```json
{
  "Checkpoints": {
    "C:/repos/AIMemory/src/AIMemory.Api/Program.cs": {
      "LastProcessedOffset": 1,
      "LastProcessedTimestamp": "2026-03-10T...",
      "FileSize": 24680,
      "FileMtime": "2026-03-10T12:34:56Z",
      "ContentHash": "a1b2c3d4..."
    }
  }
}
```

### Step 6: Add `ContentHash` to `Checkpoint`

```csharp
public class Checkpoint
{
    public long LastProcessedOffset { get; set; }
    public DateTimeOffset? LastProcessedTimestamp { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset? FileMtime { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? ContentHash { get; set; }  // NEW — SHA256, used by code adapter
}
```

This is backward-compatible — existing log adapter checkpoints just won't have this field and it deserializes as null.

### Step 7: Add new event types to `AIMemory.Models/Events/`

#### 7a. `CodeFileUpsertEvent.cs`
```csharp
public class CodeFileUpsertEvent
{
    public string RepositoryName { get; set; }
    public string SourceType { get; set; }      // "local"
    public string SourcePath { get; set; }      // root folder path
    public string FilePath { get; set; }        // relative path within repo
    public string Language { get; set; }
    public long FileSize { get; set; }
    public string ContentHash { get; set; }
    public string? MachineName { get; set; }    // which machine indexed this
}
```

#### 7b. `CodeSymbolBatchEvent.cs`
```csharp
public class CodeSymbolBatchEvent
{
    public string RepositoryName { get; set; }
    public string FilePath { get; set; }
    public List<CodeSymbolEvent> Symbols { get; set; } = [];
}

public class CodeSymbolEvent
{
    public string SymbolKey { get; set; }
    public string Name { get; set; }
    public string QualifiedName { get; set; }
    public string Kind { get; set; }
    public string? Signature { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentSymbolKey { get; set; }
}
```

### Step 8: Add project reference from Ingestor to CodeIndex

`AIMemory.Ingestor.csproj`:
```xml
<ProjectReference Include="..\AIMemory.CodeIndex\AIMemory.CodeIndex.csproj" />
```

### Step 9: Create `CodeAdapter.cs`

```
SourceName => "code-index"
```

**`DiscoverFiles(SourceConfig config)`:**
- Walk each `WatchPath` using `FileFilter` from `AIMemory.CodeIndex`
- Filter to supported extensions using `ParserRegistry.IsSupported()`
- Skip binary files via `FileFilter.IsBinaryFile()`
- Return list of absolute file paths

**`ReadNewRecords(string filePath, Checkpoint? checkpoint)`:**
- `stat()` the file to get mtime and size
- If checkpoint exists AND `FileMtime` matches AND `FileSize` matches → yield nothing (skip, zero reads)
- If mtime/size differ → read file, compute SHA256
  - If checkpoint exists AND `ContentHash` matches → yield nothing (touched but unchanged)
  - Otherwise → yield ONE `RawRecord` with:
    - `Line` = "" (unused — code adapter reads the file in `ParseRecord`)
    - `Offset` = 1 (signals "new data")
    - Also stash the computed hash and content somewhere accessible for `ParseRecord`

**Implementation detail:** Since `ISourceAdapter` is stateless between `ReadNewRecords` and `ParseRecord`, store the pre-read content and hash in a `ConcurrentDictionary<string, (string Content, string Hash)>` field on the adapter. `ParseRecord` retrieves and removes it.

**`ParseRecord(RawRecord raw)`:**
- Retrieve pre-read content from the cache
- Determine which `WatchPath` contains this file → compute relative path and repo name
- Get parser from `ParserRegistry`
- Emit `CodeFileUpsert` event with file metadata + hash
- If symbols were parsed, emit `CodeSymbolBatch` event

**Worker checkpoint update:** After processing, the Worker saves:
```csharp
_checkpointStore.SaveCheckpoint(filePath, new Checkpoint
{
    LastProcessedOffset = 1,
    LastProcessedTimestamp = DateTimeOffset.UtcNow,
    FileSize = fileInfo.Length,
    FileMtime = fileInfo.LastWriteTimeUtc,
    ContentHash = computedHash  // code adapter sets this
});
```

The existing Worker code already saves `FileSize` and `FileMtime`. We just need the adapter to communicate the hash back. Since `Worker.ProcessSourceAsync` creates the checkpoint from `FileInfo`, we need a small change:

**Option:** Add `ContentHash` to `RawRecord` (it's already a bag of data the adapter produces). The Worker can pass it through to the checkpoint. This keeps things clean.

### Step 10: Register in Ingestor `Program.cs`

```csharp
builder.Services.AddSingleton<FileFilter>();
builder.Services.AddSingleton<ILanguageParser, CSharpParser>();
builder.Services.AddSingleton<ILanguageParser, PythonParser>();
builder.Services.AddSingleton<ILanguageParser, TypeScriptParser>();
builder.Services.AddSingleton<ILanguageParser, GoParser>();
builder.Services.AddSingleton<ParserRegistry>(sp =>
    new ParserRegistry(sp.GetServices<ILanguageParser>()));
builder.Services.AddSingleton<ISourceAdapter, CodeAdapter>();
```

### Step 11: Handle new event types in API batch ingest endpoint

Add to the `switch (evt.Type)` block in `Program.cs`:

**`"CodeFileUpsert"`:**
- Deserialize `CodeFileUpsertEvent`
- Upsert `CodeRepository` by name (using `ICodeIndexRepository.UpsertRepositoryAsync`)
- Upsert `CodeFile` by repo+path (using `ICodeIndexRepository.UpsertFileAsync`)

**`"CodeSymbolBatch"`:**
- Deserialize `CodeSymbolBatchEvent`
- Look up repo by name, file by repo+path
- Call `ICodeIndexRepository.UpsertSymbolsAsync` to replace symbols for that file

---

## Part 3: API Key Management

### Step 12: `ApiKey` entity — `AIMemory.Models/Entities/ApiKey.cs`

```csharp
public class ApiKey
{
    public Guid ApiKeyId { get; set; }
    public string Name { get; set; }
    public string KeyHash { get; set; }         // SHA256 of raw key
    public string KeyPrefix { get; set; }       // first 8 chars for display
    public List<string> Scopes { get; set; }    // ["ingest", "code", "mcp", "admin"]
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }
}
```

**Scopes:**
| Scope | UI Label | Access |
|---|---|---|
| `ingest` | Log Ingestor | `/api/ingest/batch` (log events) |
| `code` | Code Index | `/api/ingest/batch` (code events) + `/api/code/*` |
| `mcp` | MCP Server | `/api/sessions/*`, `/api/search`, `/api/code/*` (read) |
| `admin` | Full Access | Everything |

### Step 13: DbContext + Migration

- `DbSet<ApiKey> ApiKeys`
- Table: `api_keys`, snake_case columns
- `Scopes` stored as JSON text (same converter as `Session.Tags`)
- Indexes: `KeyHash` (unique), `IsActive`
- Combined migration: `AddApiKeysAndMachineName` (merges Steps 5 + 13)

### Step 14: `IApiKeyRepository` + `ApiKeyRepository`

```csharp
public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey key);
    Task<ApiKey?> GetByHashAsync(string keyHash);
    Task<List<ApiKey>> ListAsync();
    Task<ApiKey?> GetByIdAsync(Guid id);
    Task UpdateLastUsedAsync(Guid id);
    Task RevokeAsync(Guid id);
    Task DeleteAsync(Guid id);
}
```

### Step 15: Rewrite `ApiKeyAuthMiddleware`

- Inject `IServiceScopeFactory` (middleware is singleton, repos are scoped)
- On each request with `X-API-Key` header:
  1. SHA256 hash the provided key
  2. Look up in DB via `IApiKeyRepository.GetByHashAsync`
  3. Check `IsActive` and `ExpiresAt`
  4. Check scope against requested path
  5. Fire-and-forget `UpdateLastUsedAsync`
- **Backward compat:** Legacy `AIMEMORY_API_KEY` env var still works as admin-scope fallback
- **Caching:** Cache valid key hashes for 60 seconds in-memory to avoid DB hit on every request

**Scope → Path mapping:**
| Path Pattern | Required Scope |
|---|---|
| `/api/ingest/batch` | `ingest` OR `code` OR `admin` |
| `/api/code/*` | `code` OR `mcp` OR `admin` |
| `/api/sessions/*`, `/api/search` | `mcp` OR `admin` |
| `/api/keys/*` | `admin` (or cookie auth) |
| `/api/stats`, `/api/ingestion-log`, `/api/health` | any valid key |

### Step 16: API Key management endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/api/keys` | List all keys (never returns raw key) |
| POST | `/api/keys` | Create key. Returns raw key ONCE. |
| GET | `/api/keys/{id}` | Get key details |
| PATCH | `/api/keys/{id}` | Update name, scopes, isActive |
| DELETE | `/api/keys/{id}` | Permanently delete key |

**Key format:** `aimemory_` + 32 random hex chars
**Storage:** Only SHA256 hash stored. Prefix (first 8 hex chars after `aimemory_`) stored for identification.

### Step 17: React UI — `ApiKeys.tsx`

New page at `/keys` in sidebar nav.

**Table columns:** Name | Key (`aimemory_a1b2...`) | Scopes (badges) | Status | Last Used | Actions

**Create dialog:**
- Name text input
- Scope checkboxes with friendly labels: "Log Ingestor", "Code Index", "MCP Server", "Full Access"
- After create: display raw key in monospace copyable field with warning "Save this key — it won't be shown again"

**Actions:** Revoke (sets inactive, keeps for audit) | Delete (permanent)

### Step 18: Wire up React

- Add API functions to `client.ts`
- Add "API Keys" to `navItems` in `Layout.tsx`
- Add `/keys` route in `App.tsx`

---

## Implementation Order

### Phase 1 — Machine Name (Steps 1-5)
1. Add `MachineName` to `BatchIngestRequest`
2. Auto-populate in Worker, default `ClientId` to `Environment.MachineName`
3. Add `MachineName` to `Session` and `IngestionLogEntry` entities
4. Propagate in API batch handler
5. (Migration deferred to Phase 3 to combine)

### Phase 2 — Code Adapter (Steps 6-11)
6. Add `ContentHash` to `Checkpoint`
7. Create event types (`CodeFileUpsertEvent`, `CodeSymbolBatchEvent`)
8. Add Ingestor → CodeIndex project reference
9. Create `CodeAdapter.cs` with intelligent change tracking
10. Register in Ingestor `Program.cs`
11. Handle new events in API batch endpoint

### Phase 3 — API Key Model + Auth (Steps 12-15)
12. Create `ApiKey` entity
13. DbContext config + combined migration (`AddApiKeysAndMachineName`)
14. Create `IApiKeyRepository` + `ApiKeyRepository`
15. Rewrite `ApiKeyAuthMiddleware`

### Phase 4 — API Key Endpoints + UI (Steps 16-18)
16. Add `/api/keys` CRUD endpoints
17. Create `ApiKeys.tsx` page
18. Wire up client, layout, routing

---

## File Changes Summary

### New Files
| File | Description |
|---|---|
| `src/AIMemory.Models/Events/CodeFileUpsertEvent.cs` | Code file change event |
| `src/AIMemory.Models/Events/CodeSymbolBatchEvent.cs` | Symbol batch event |
| `src/AIMemory.Models/Entities/ApiKey.cs` | API key entity |
| `src/AIMemory.Models/Dtos/ApiKeyDtos.cs` | Key management DTOs |
| `src/AIMemory.Ingestor/Adapters/CodeAdapter.cs` | Code ingestion adapter |
| `src/AIMemory.Data/Repositories/IApiKeyRepository.cs` | Interface |
| `src/AIMemory.Data/Repositories/ApiKeyRepository.cs` | Implementation |
| `src/aimemory.client/src/pages/ApiKeys.tsx` | Key management page |

### Modified Files
| File | Change |
|---|---|
| `src/AIMemory.Models/Dtos/BatchIngestRequest.cs` | Add `MachineName` field |
| `src/AIMemory.Models/Entities/Session.cs` | Add `MachineName` field |
| `src/AIMemory.Models/Entities/IngestionLogEntry.cs` | Add `MachineName` field |
| `src/AIMemory.Ingestor/Checkpointing/ICheckpointStore.cs` | Add `ContentHash` to `Checkpoint` |
| `src/AIMemory.Ingestor/Worker.cs` | Set `MachineName`, pass `ContentHash` to checkpoint |
| `src/AIMemory.Ingestor/Adapters/ISourceAdapter.cs` | Add `ContentHash` to `RawRecord` |
| `src/AIMemory.Ingestor/AIMemory.Ingestor.csproj` | Add CodeIndex reference |
| `src/AIMemory.Ingestor/Program.cs` | Register CodeAdapter + CodeIndex services |
| `src/AIMemory.Ingestor/Configuration/IngestorConfig.cs` | Default `ClientId` to `Environment.MachineName` |
| `src/AIMemory.Api/Program.cs` | Code event handlers + key endpoints + DI |
| `src/AIMemory.Api/Middleware/ApiKeyAuthMiddleware.cs` | DB-backed keys + scopes |
| `src/AIMemory.Data/AIMemoryDbContext.cs` | Add `ApiKeys` DbSet + `MachineName` columns + ApiKey config |
| `src/aimemory.client/src/api/client.ts` | Key management functions |
| `src/aimemory.client/src/components/Layout.tsx` | Add "API Keys" nav |
| `src/aimemory.client/src/App.tsx` | Add `/keys` route |

### Migrations
- `AddApiKeysAndMachineName` — adds `api_keys` table + `machine_name` column to `sessions` and `ingestion_log`

---

## Key Design Decisions

1. **No one-time ingest mode** — The code adapter always runs on the polling schedule. Intelligent tracking in the JSON checkpoint file means it only re-processes files whose mtime+size changed, and only sends events when the content hash actually differs. First run indexes everything; subsequent runs are near-silent.

2. **Machine name is auto-detected** — Uses `Environment.MachineName`. No manual config needed. Stored on sessions and ingestion log entries so you can filter/search by machine.

3. **Three-tier change detection** minimizes chatter:
   - Tier 1: `stat()` check (mtime + size) — O(1), no file read
   - Tier 2: SHA256 hash comparison — only if mtime changed
   - Tier 3: Event generation — only if hash changed

4. **Content hash stored in checkpoint JSON** — Not a new file. Uses the existing `JsonCheckpointStore` with an added `ContentHash` field on `Checkpoint`. Backward-compatible (existing checkpoints just have null hash).

5. **Legacy API key env var preserved** — Existing deployments keep working. New keys managed through UI.
