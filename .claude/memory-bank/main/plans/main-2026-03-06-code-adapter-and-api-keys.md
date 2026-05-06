# Plan: Code Ingestor Adapter + API Key Management UI

**Date:** 2026-03-06
**Branch:** main
**Status:** PLAN

---

## Goal

Two deliverables:

1. **Code Adapter** — A new `ISourceAdapter` in `AIMemory.Ingestor` that runs on a schedule (just like the log adapters), detects code file changes via content hash, parses symbols using the `AIMemory.CodeIndex` parsers, and pushes `CodeFileUpsert`/`CodeSymbolUpsert` events through the existing batch ingest pipeline.

2. **API Key Management** — A proper API key system replacing the single env-var key. Multiple keys with scopes, stored in the database, managed via the web UI. Both the log ingestor and code ingestor authenticate with these keys.

---

## Part 1: Code Adapter in AIMemory.Ingestor

### Current Architecture

The ingestor has a clean adapter pattern:
- `ISourceAdapter` — `DiscoverFiles()`, `ReadNewRecords()`, `ParseRecord()`
- `Worker` — scans on interval, calls adapters, sends batches via `IAIMemoryClient`
- Checkpointing — tracks last-processed offset per file
- Existing adapters: `ClaudeCodeAdapter` (JSONL log tailing), `CodexCliAdapter` (JSONL + log)

### Challenge

The existing adapter interface is line-oriented (`RawRecord.Line`), designed for JSONL log files. Code files aren't line-based records — they're whole files that need to be parsed as a unit. We need to adapt this without breaking the existing adapters.

### Design Decision: Whole-File-As-Record Approach

The code adapter will treat each source code file as a single "record". The `RawRecord.Line` field will contain the full file content (or just the file path — the adapter knows how to parse it). The checkpoint tracks the content hash so unchanged files are skipped.

### Step 1: Add new event types to `AIMemory.Models/Events/`

#### 1a. `CodeFileUpsertEvent.cs`
```csharp
public class CodeFileUpsertEvent
{
    public string RepositoryName { get; set; }  // maps to CodeRepository.Name
    public string SourceType { get; set; }      // "local"
    public string SourcePath { get; set; }      // root folder path
    public string FilePath { get; set; }        // relative path within repo
    public string Language { get; set; }
    public long FileSize { get; set; }
    public string ContentHash { get; set; }
}
```

#### 1b. `CodeSymbolBatchEvent.cs`
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

### Step 2: Add `CodeAdapter` to `AIMemory.Ingestor/Adapters/`

#### 2a. `CodeAdapter.cs`

```
SourceName => "code-index"
```

**`DiscoverFiles(SourceConfig config)`:**
- Walk each `WatchPath` using `FileFilter` from `AIMemory.CodeIndex`
- Filter to supported extensions using `ParserRegistry`
- Skip binary files
- Return list of absolute file paths

**`ReadNewRecords(string filePath, Checkpoint? checkpoint)`:**
- Compute SHA256 of file content
- Compare against checkpoint's hash (stored in a custom field — see Step 2b)
- If hash matches, skip (no changes)
- If hash differs or no checkpoint, yield a single `RawRecord` where:
  - `Line` = content hash (lightweight — actual content read during `ParseRecord`)
  - `Offset` = 1 (increments force checkpoint update)

**`ParseRecord(RawRecord raw)`:**
- Read full file content from `raw.FilePath`
- Determine root path from the `SourceConfig.WatchPaths` (find which watch path contains this file)
- Compute relative path
- Parse using `ParserRegistry.GetParser(filePath)`
- Emit `CodeFileUpsert` event with file metadata
- Emit `CodeSymbolBatch` event with all parsed symbols

#### 2b. Checkpoint Enhancement

The existing `Checkpoint` class has `FileSize` and `FileMtime` but not a content hash. Rather than modifying the shared checkpoint model, the code adapter will use `FileMtime` for fast skip (if mtime hasn't changed, skip without hashing) and fall back to content hash comparison. This means:
- First check: `fileMtime == checkpoint.FileMtime` → skip
- Second check: compute hash, compare with stored hash → skip if same
- Store the content hash in the `RawRecord.Line` field which the checkpoint doesn't track, but we'll store it as the offset (use a hash→long mapping or just always reprocess when mtime changes)

**Simplified approach:** Use mtime-only for change detection (good enough, matches how `git status` works). If mtime changed, re-parse the file. No hash needed for the ingestor — the API-side upsert handles deduplication via content hash in `CodeFile.ContentHash`.

### Step 3: Add project reference from Ingestor to CodeIndex

`AIMemory.Ingestor.csproj` needs:
```xml
<ProjectReference Include="..\AIMemory.CodeIndex\AIMemory.CodeIndex.csproj" />
```

Register `FileFilter`, parsers, `ParserRegistry`, and `CodeAdapter` in `Program.cs`.

### Step 4: Handle new event types in `AIMemory.Api/Program.cs` batch ingest endpoint

Add cases to the `switch (evt.Type)` block:

**`"CodeFileUpsert"`:**
- Deserialize `CodeFileUpsertEvent`
- Upsert `CodeRepository` by name (create if not exists)
- Upsert `CodeFile` by repo+path (update hash, size, language)

**`"CodeSymbolBatch"`:**
- Deserialize `CodeSymbolBatchEvent`
- Look up repo by name, file by path
- Replace all symbols for that file with the new batch

### Step 5: Add `SourceConfig` for code in ingestor config

Example `appsettings.json` entry:
```json
{
  "Ingestor": {
    "Sources": [
      {
        "Name": "code-index",
        "Enabled": true,
        "WatchPaths": ["C:\\Users\\erica\\source\\repos\\AIMemory"],
        "FilePatterns": ["*.*"],
        "Segment": "code"
      }
    ]
  }
}
```

The `FilePatterns` are less important here since `FileFilter` + `ParserRegistry` handle filtering. The adapter will ignore `FilePatterns` and use its own filtering.

---

## Part 2: API Key Management

### Current State

- Single API key: env var `AIMEMORY_API_KEY` or config `AIMemory:ApiKey`
- `ApiKeyAuthMiddleware` compares `X-API-Key` header against this single value
- No scoping, no revocation, no management UI

### New Design

#### Step 6: `ApiKey` entity — `AIMemory.Models/Entities/ApiKey.cs`

```csharp
public class ApiKey
{
    public Guid ApiKeyId { get; set; }
    public string Name { get; set; }            // human-readable label
    public string KeyHash { get; set; }         // SHA256 hash of the key (never store raw)
    public string KeyPrefix { get; set; }       // first 8 chars for identification (e.g. "ab12cd34")
    public List<string> Scopes { get; set; }    // ["ingest", "code", "mcp", "admin"]
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? CreatedBy { get; set; }      // username who created it
}
```

**Scopes:**
- `ingest` — can call `/api/ingest/batch` (log ingestor)
- `code` — can call `/api/ingest/batch` for code events AND `/api/code/*` endpoints
- `mcp` — can call all read endpoints (sessions, search, code queries) + write endpoints (create session, append message, etc.)
- `admin` — full access (same as cookie auth)

A key can have multiple scopes. The "Log Ingestor" vs "Code Index" distinction the user asked about maps to `ingest` vs `code` scopes.

#### Step 7: DbContext + Migration

- Add `DbSet<ApiKey> ApiKeys` to `AIMemoryDbContext`
- Table: `api_keys`, snake_case columns
- `List<string> Scopes` stored as JSON text (same pattern as `Session.Tags`)
- Indexes on: `KeyHash` (unique), `IsActive`
- Migration: `AddApiKeys`

#### Step 8: API Key repository — `AIMemory.Data/Repositories/`

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

#### Step 9: Key generation utility

Keys are generated server-side:
- Format: `aimemory_` + 32 random hex chars (e.g. `aimemory_a1b2c3d4e5f6...`)
- The raw key is returned ONCE at creation time
- Only the SHA256 hash is stored in the database
- The prefix (`a1b2c3d4`) is stored for display/identification

#### Step 10: Update `ApiKeyAuthMiddleware`

Replace single-key check with database lookup:

```csharp
public async Task InvokeAsync(HttpContext context)
{
    // ... existing skip logic for non-API paths ...

    // Cookie auth (existing)
    if (context.User.Identity?.IsAuthenticated == true) { await _next(context); return; }

    // API key auth — look up in database
    if (context.Request.Headers.TryGetValue("X-API-Key", out var providedKey))
    {
        var keyHash = SHA256Hash(providedKey!);
        var apiKey = await _apiKeyRepo.GetByHashAsync(keyHash);

        if (apiKey != null && apiKey.IsActive && (apiKey.ExpiresAt == null || apiKey.ExpiresAt > DateTimeOffset.UtcNow))
        {
            // Check scope against requested path
            if (HasRequiredScope(apiKey.Scopes, context.Request.Path, context.Request.Method))
            {
                // Update last used (fire-and-forget)
                _ = _apiKeyRepo.UpdateLastUsedAsync(apiKey.ApiKeyId);
                await _next(context);
                return;
            }
        }
    }

    // ... existing 401 response ...
}
```

**Scope checking logic:**
| Path Pattern | Required Scope |
|---|---|
| `/api/ingest/batch` | `ingest` or `code` or `admin` |
| `/api/code/*` | `code` or `admin` |
| `/api/sessions/*`, `/api/search` | `mcp` or `admin` |
| `/api/keys/*` | `admin` (or cookie auth) |
| `/api/stats`, `/api/ingestion-log` | any valid key or cookie |

**Backward compatibility:** Keep supporting the legacy single env-var key (`AIMEMORY_API_KEY`) as a fallback with full admin scope. This means existing ingestor deployments don't break.

#### Step 11: API Key management endpoints — `AIMemory.Api/Program.cs`

All require cookie auth (admin UI) or admin-scoped API key:

| Method | Path | Description |
|---|---|---|
| GET | `/api/keys` | List all keys (returns name, prefix, scopes, active, lastUsed — never the raw key) |
| POST | `/api/keys` | Create a new key. Body: `{ name, scopes }`. Returns the raw key ONCE. |
| GET | `/api/keys/{id}` | Get key details |
| PATCH | `/api/keys/{id}` | Update name, scopes, active status |
| DELETE | `/api/keys/{id}` | Delete a key |

**Create key request:**
```json
{
  "name": "Eric's Log Ingestor",
  "scopes": ["ingest"]
}
```

**Create key response (only time raw key is shown):**
```json
{
  "apiKeyId": "...",
  "name": "Eric's Log Ingestor",
  "key": "aimemory_a1b2c3d4e5f6...",
  "keyPrefix": "a1b2c3d4",
  "scopes": ["ingest"],
  "createdAt": "..."
}
```

#### Step 12: React UI — `src/aimemory.client/src/pages/ApiKeys.tsx`

A new page in the sidebar navigation: **"API Keys"**

**Layout:**
- Header: "API Keys" with a "Create Key" button
- Table showing all keys:
  | Name | Key Prefix | Scopes | Status | Last Used | Created | Actions |
  |---|---|---|---|---|---|---|
  | Eric's Log Ingestor | `a1b2c3d4...` | ingest | Active | 2 hours ago | Mar 6 | Revoke / Delete |
  | Code Index - AIMemory | `e5f6a7b8...` | code | Active | never | Mar 6 | Revoke / Delete |

**Create Key Modal/Dialog:**
- Name input (text field)
- Scope checkboxes: Log Ingestor, Code Index, MCP, Admin
- "Create" button
- After creation: shows the raw key in a copyable field with a warning "This key will only be shown once"

**Actions:**
- **Revoke** — sets `IsActive = false` (key stops working but is kept for audit)
- **Delete** — permanently removes the key

#### Step 13: Update `src/aimemory.client/src/api/client.ts`

Add API key client functions:
```typescript
export interface ApiKeyResponse {
  apiKeyId: string; name: string; keyPrefix: string;
  scopes: string[]; isActive: boolean;
  createdAt: string; lastUsedAt?: string; expiresAt?: string;
}
export interface CreateApiKeyResponse extends ApiKeyResponse { key: string }

export const getApiKeys = () => request<ApiKeyResponse[]>('/api/keys')
export const createApiKey = (data: { name: string; scopes: string[] }) =>
  request<CreateApiKeyResponse>('/api/keys', { method: 'POST', body: JSON.stringify(data) })
export const revokeApiKey = (id: string) =>
  request<void>(`/api/keys/${id}`, { method: 'PATCH', body: JSON.stringify({ isActive: false }) })
export const deleteApiKey = (id: string) =>
  request<void>(`/api/keys/${id}`, { method: 'DELETE' })
```

#### Step 14: Update Layout navigation

Add "API Keys" to `navItems` in `Layout.tsx`:
```typescript
const navItems = [
  { to: '/', label: 'Dashboard' },
  { to: '/sessions', label: 'Sessions' },
  { to: '/search', label: 'Search' },
  { to: '/logs', label: 'Logs' },
  { to: '/keys', label: 'API Keys' },
]
```

Add route in `App.tsx`:
```tsx
<Route path="/keys" element={<ApiKeys />} />
```

---

## Implementation Order

### Phase 1 — Event Types + Code Adapter (Steps 1-3)
1. Create `CodeFileUpsertEvent.cs` and `CodeSymbolBatchEvent.cs`
2. Add project reference from Ingestor to CodeIndex
3. Create `CodeAdapter.cs` implementing `ISourceAdapter`
4. Register adapter and CodeIndex services in Ingestor `Program.cs`

### Phase 2 — API-side Code Event Handling (Step 4)
5. Add `CodeFileUpsert` and `CodeSymbolBatch` handlers to batch ingest endpoint

### Phase 3 — API Key Model + Database (Steps 6-8)
6. Create `ApiKey` entity
7. Add to DbContext + create migration
8. Create `IApiKeyRepository` + `ApiKeyRepository`

### Phase 4 — API Key Auth (Steps 9-10)
9. Implement key generation utility
10. Rewrite `ApiKeyAuthMiddleware` to use database + scopes

### Phase 5 — API Key Endpoints (Step 11)
11. Add `/api/keys` CRUD endpoints

### Phase 6 — React UI (Steps 12-14)
12. Create `ApiKeys.tsx` page
13. Add API client functions
14. Add navigation + route

---

## File Changes Summary

### New Files
| File | Description |
|---|---|
| `src/AIMemory.Models/Events/CodeFileUpsertEvent.cs` | Event for code file changes |
| `src/AIMemory.Models/Events/CodeSymbolBatchEvent.cs` | Event for symbol batches |
| `src/AIMemory.Models/Entities/ApiKey.cs` | API key entity |
| `src/AIMemory.Models/Dtos/ApiKeyDtos.cs` | Request/response DTOs for key management |
| `src/AIMemory.Ingestor/Adapters/CodeAdapter.cs` | Code ingestion adapter |
| `src/AIMemory.Data/Repositories/IApiKeyRepository.cs` | Repository interface |
| `src/AIMemory.Data/Repositories/ApiKeyRepository.cs` | Repository implementation |
| `src/aimemory.client/src/pages/ApiKeys.tsx` | API key management page |

### Modified Files
| File | Change |
|---|---|
| `src/AIMemory.Ingestor/AIMemory.Ingestor.csproj` | Add CodeIndex project reference |
| `src/AIMemory.Ingestor/Program.cs` | Register CodeAdapter, FileFilter, parsers |
| `src/AIMemory.Api/Program.cs` | Add code event handlers + key endpoints + DI |
| `src/AIMemory.Api/Middleware/ApiKeyAuthMiddleware.cs` | Rewrite for DB-backed keys + scopes |
| `src/AIMemory.Data/AIMemoryDbContext.cs` | Add ApiKeys DbSet + configuration |
| `src/aimemory.client/src/api/client.ts` | Add key management API functions |
| `src/aimemory.client/src/components/Layout.tsx` | Add "API Keys" nav item |
| `src/aimemory.client/src/App.tsx` | Add `/keys` route |

### Migrations
- `AddApiKeys` — adds `api_keys` table

---

## Scope Labels for UI

The user asked to "pick between the 2 — Ingestor or Code Index." In the UI, the scope checkboxes will use friendly labels:

| Scope Value | UI Label | Description |
|---|---|---|
| `ingest` | Log Ingestor | Allows pushing AI conversation logs |
| `code` | Code Index | Allows pushing code index data and querying code |
| `mcp` | MCP Server | Allows MCP tools (sessions, search, code queries) |
| `admin` | Full Access | Unrestricted API access |

---

## Notes

- The legacy `AIMEMORY_API_KEY` env var continues to work (admin scope) for backward compatibility
- API keys are hashed with SHA256 before storage — raw keys are never persisted
- The code adapter reuses all existing ingestor infrastructure (Worker, checkpointing, outbox, batching)
- No changes needed to `AIMemory.Mcp` — it already uses API key auth via `X-API-Key` header
