# Plan: Code Ingestor (jCodeMunch-inspired)

**Date:** 2026-03-06
**Branch:** main
**Status:** PLAN
**Reference:** https://github.com/jgravelle/jcodemunch-mcp

---

## Goal

Add a code indexing and retrieval system to AIMemory, inspired by jCodeMunch MCP. This enables AI agents to explore codebases through structured symbol retrieval (functions, classes, methods) rather than brute-force file reading. The system will be implemented in C# and exposed through both the AIMemory API and MCP server.

---

## Architecture Overview

```
Code Repo (local or GitHub)
  -> AIMemory.CodeIndex (new library) — parse, index, store
  -> AIMemory.Api — REST endpoints for code queries
  -> AIMemory.Mcp — MCP tools for agent access
  -> SQLite/PostgreSQL — indexed symbol storage
```

**Key difference from jCodeMunch:** Instead of a standalone MCP server with local JSON file storage, this integrates directly into the existing AIMemory ecosystem with database-backed storage, API key auth, and the existing dual-provider DB pattern.

---

## New Project: `AIMemory.CodeIndex`

A class library responsible for all code parsing, indexing, and retrieval logic.

### Step 1: Create project and add to solution

1. Create `src/AIMemory.CodeIndex/AIMemory.CodeIndex.csproj` (net10.0 class library)
2. Add project reference from `AIMemory.Api` and `AIMemory.Mcp` to `AIMemory.CodeIndex`
3. Add project reference from `AIMemory.CodeIndex` to `AIMemory.Models` and `AIMemory.Data`
4. Add to `src/AIMemory.slnx`

### Step 2: NuGet dependencies

- **TreeSitter bindings:** Use `tree_sitter_sharp` or `TreeSitter.Bindings` for AST parsing (evaluate available C# tree-sitter bindings — if none are mature enough, fall back to Roslyn for C# and regex-based extraction for other languages)
- Alternative: Use **Microsoft.CodeAnalysis (Roslyn)** for C#/VB, and a lightweight regex/line-based parser for other languages (Python, JS, TS, Go, etc.)

### Step 3: Core models — `AIMemory.Models`

Add new entity and DTO classes:

#### 3a. Entity: `CodeRepository`
```csharp
// Represents an indexed code repository
public class CodeRepository
{
    public Guid RepositoryId { get; set; }
    public string Name { get; set; }          // e.g. "owner/repo" or folder name
    public string SourceType { get; set; }    // "github" | "local"
    public string SourcePath { get; set; }    // local path or github URL
    public string? DefaultBranch { get; set; }
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

#### 3b. Entity: `CodeFile`
```csharp
// A single file within an indexed repository
public class CodeFile
{
    public Guid FileId { get; set; }
    public Guid RepositoryId { get; set; }
    public string FilePath { get; set; }      // relative path within repo
    public string Language { get; set; }       // "csharp", "python", etc.
    public long FileSize { get; set; }
    public string ContentHash { get; set; }   // SHA256 for change detection
    public DateTimeOffset IndexedAt { get; set; }

    public CodeRepository? Repository { get; set; }
    public List<CodeSymbol> Symbols { get; set; } = [];
}
```

#### 3c. Entity: `CodeSymbol`
```csharp
// A parsed symbol (function, class, method, etc.)
public class CodeSymbol
{
    public Guid SymbolId { get; set; }
    public Guid FileId { get; set; }
    public Guid RepositoryId { get; set; }
    public string SymbolKey { get; set; }     // stable ID: "path::QualifiedName#kind"
    public string Name { get; set; }          // simple name
    public string QualifiedName { get; set; } // full qualified name
    public string Kind { get; set; }          // function, class, method, property, etc.
    public string? Signature { get; set; }    // e.g. "public async Task<string> Foo(int x)"
    public string? Summary { get; set; }      // one-line description (optional LLM-generated)
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentSymbolKey { get; set; } // for nesting (method inside class)
    public DateTimeOffset IndexedAt { get; set; }

    public CodeFile? File { get; set; }
}
```

#### 3d. DTOs
```
IndexRepoRequest { Path, SourceType, Name? }
IndexFolderRequest { FolderPath, Name? }
CodeRepoResponse { RepositoryId, Name, FileCount, SymbolCount, IndexedAt }
FileTreeResponse { Files: List<FileTreeNode> }
FileOutlineResponse { FilePath, Symbols: List<SymbolOutline> }
SymbolResponse { SymbolKey, Name, Kind, Signature, Summary, SourceCode }
SearchSymbolsRequest { Query, RepoId?, Kind?, FilePath?, Limit }
SearchSymbolsResponse { Symbols: List<SymbolMatch> }
TextSearchRequest { Query, RepoId?, FilePath?, Limit }
TextSearchResponse { Matches: List<TextMatch> }
```

### Step 4: Database schema — `AIMemory.Data`

#### 4a. Add DbSets to `AIMemoryDbContext`
```csharp
public DbSet<CodeRepository> CodeRepositories => Set<CodeRepository>();
public DbSet<CodeFile> CodeFiles => Set<CodeFile>();
public DbSet<CodeSymbol> CodeSymbols => Set<CodeSymbol>();
```

#### 4b. Add EF configuration in `OnModelCreating`
- Table names: `code_repositories`, `code_files`, `code_symbols`
- Snake_case column naming (matching existing convention)
- Indexes on: `RepositoryId`, `FilePath`, `SymbolKey`, `Kind`, `Name`, `QualifiedName`
- Full-text index on `Name` + `QualifiedName` for symbol search

#### 4c. Add migration
- `dotnet ef migrations add AddCodeIndex`

#### 4d. Repository interfaces and implementations
```
ICodeRepository — CRUD for CodeRepository, CodeFile, CodeSymbol
  - IndexRepositoryAsync(CodeRepository repo)
  - GetRepositoryAsync(Guid id) / GetByNameAsync(string name)
  - ListRepositoriesAsync()
  - DeleteRepositoryAsync(Guid id)
  - UpsertFilesAsync(Guid repoId, List<CodeFile> files)
  - UpsertSymbolsAsync(Guid fileId, List<CodeSymbol> symbols)
  - GetFileTreeAsync(Guid repoId)
  - GetFileOutlineAsync(Guid repoId, string filePath)
  - GetSymbolAsync(string symbolKey)
  - GetSymbolsAsync(List<string> symbolKeys)
  - SearchSymbolsAsync(string query, Guid? repoId, string? kind, int limit)
  - SearchTextAsync(string query, Guid? repoId, string? filePath, int limit)
```

### Step 5: Language parsers — `AIMemory.CodeIndex/Parsers/`

#### 5a. Parser interface
```csharp
public interface ILanguageParser
{
    string Language { get; }
    string[] FileExtensions { get; }
    List<ParsedSymbol> Parse(string filePath, string content);
}

public class ParsedSymbol
{
    public string Name { get; set; }
    public string QualifiedName { get; set; }
    public string Kind { get; set; }
    public string? Signature { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentName { get; set; }
}
```

#### 5b. Roslyn-based C# parser (highest priority)
- Uses `Microsoft.CodeAnalysis.CSharp` to walk the syntax tree
- Extracts: namespaces, classes, records, structs, interfaces, methods, properties, fields, enums, delegates
- Builds qualified names: `Namespace.Class.Method`
- Captures full signature text

#### 5c. Regex/pattern-based parsers for other languages (Phase 1)
- **Python:** `def`, `class`, decorators
- **JavaScript/TypeScript:** `function`, `class`, `const/let x = () =>`, `export`
- **Go:** `func`, `type`, `struct`
- **Java:** `class`, `interface`, method signatures
- **Rust:** `fn`, `struct`, `impl`, `trait`, `enum`

These use line-by-line scanning with regex patterns to identify symbol boundaries. Not as accurate as AST parsing but good enough for outline/search.

#### 5d. Parser registry
```csharp
public class ParserRegistry
{
    Dictionary<string, ILanguageParser> _parsers; // keyed by extension
    ILanguageParser? GetParser(string filePath);
}
```

### Step 6: Indexing engine — `AIMemory.CodeIndex/IndexingService.cs`

```csharp
public class CodeIndexingService
{
    Task<CodeRepository> IndexLocalFolderAsync(string path, string? name, CancellationToken ct);
    Task<CodeRepository> IndexGitHubRepoAsync(string owner, string repo, string? branch, CancellationToken ct);
    Task<CodeRepository> ReindexAsync(Guid repositoryId, CancellationToken ct);
    Task InvalidateCacheAsync(Guid repositoryId);
}
```

**Indexing flow:**
1. Discover files (walk directory, respect `.gitignore`, skip binaries/secrets)
2. Filter by supported extensions
3. For each file: compute content hash, skip if unchanged
4. Parse symbols using appropriate `ILanguageParser`
5. Build stable symbol keys: `{relative_path}::{QualifiedName}#{kind}`
6. Upsert to database
7. Update repository stats (file count, symbol count)

**Security filtering (matching jCodeMunch):**
- Skip: `.env`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `credentials.*`, `secrets.*`
- Skip: binary files (detect via BOM/null-byte check)
- Skip: files > configurable max size (default 1MB)
- Skip: `node_modules/`, `bin/`, `obj/`, `.git/`, `vendor/`, `__pycache__/`
- Path traversal prevention on all inputs

### Step 7: Symbol retrieval service — `AIMemory.CodeIndex/CodeQueryService.cs`

```csharp
public class CodeQueryService
{
    Task<List<CodeRepoResponse>> ListRepositoriesAsync();
    Task<FileTreeResponse> GetFileTreeAsync(Guid repoId);
    Task<FileOutlineResponse> GetFileOutlineAsync(Guid repoId, string filePath);
    Task<SymbolResponse> GetSymbolAsync(Guid repoId, string symbolKey);
    Task<List<SymbolResponse>> GetSymbolsAsync(Guid repoId, List<string> symbolKeys);
    Task<List<SymbolMatch>> SearchSymbolsAsync(string query, Guid? repoId, string? kind, int limit);
    Task<List<TextMatch>> SearchTextAsync(string query, Guid? repoId, string? filePath, int limit);
    Task<RepoOutlineResponse> GetRepoOutlineAsync(Guid repoId);
}
```

`GetSymbol` reads the source file from disk using stored byte offsets for O(1) retrieval (matching jCodeMunch's approach). Falls back to line-range extraction if byte offsets are unavailable.

---

## API Endpoints — `AIMemory.Api`

### Step 8: Add code endpoints to `Program.cs`

All under `/api/code/` prefix:

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/code/index-folder` | Index a local folder |
| POST | `/api/code/index-github` | Index a GitHub repo (clone + index) |
| GET | `/api/code/repos` | List indexed repositories |
| GET | `/api/code/repos/{repoId}` | Get repo details |
| DELETE | `/api/code/repos/{repoId}` | Remove indexed repo |
| POST | `/api/code/repos/{repoId}/reindex` | Re-index a repo |
| GET | `/api/code/repos/{repoId}/tree` | Get file tree |
| GET | `/api/code/repos/{repoId}/outline?file={path}` | Get file symbol outline |
| GET | `/api/code/repos/{repoId}/outline` | Get high-level repo outline |
| GET | `/api/code/repos/{repoId}/symbol?key={symbolKey}` | Get symbol source code |
| POST | `/api/code/repos/{repoId}/symbols` | Batch get symbols (body: list of keys) |
| GET | `/api/code/search/symbols?q={query}&repo={id}&kind={kind}` | Search symbols |
| GET | `/api/code/search/text?q={query}&repo={id}&file={path}` | Full-text search in code |

All endpoints require API key auth (existing `ApiKeyAuthMiddleware`).

---

## MCP Tools — `AIMemory.Mcp`

### Step 9: Add code tools to MCP server

Create `AIMemory.Mcp/Tools/CodeTools.cs`:

```csharp
[McpServerToolType]
public class CodeTools
{
    [McpServerTool, Description("Index a local code folder for symbol retrieval")]
    Task<string> IndexFolder(string folderPath, string? name);

    [McpServerTool, Description("List all indexed code repositories")]
    Task<string> ListCodeRepos();

    [McpServerTool, Description("Get file tree of an indexed repository")]
    Task<string> GetFileTree(string repoName);

    [McpServerTool, Description("Get symbol outline for a file in an indexed repo")]
    Task<string> GetFileOutline(string repoName, string filePath);

    [McpServerTool, Description("Get full source code of a specific symbol by its key")]
    Task<string> GetSymbol(string repoName, string symbolKey);

    [McpServerTool, Description("Batch retrieve multiple symbols by their keys")]
    Task<string> GetSymbols(string repoName, string symbolKeysJson);

    [McpServerTool, Description("Search for symbols by name, with optional kind filter")]
    Task<string> SearchSymbols(string query, string? repoName, string? kind, int limit = 20);

    [McpServerTool, Description("Full-text search within indexed code")]
    Task<string> SearchCodeText(string query, string? repoName, string? filePath, int limit = 20);

    [McpServerTool, Description("Get high-level overview of an indexed repository")]
    Task<string> GetRepoOutline(string repoName);

    [McpServerTool, Description("Remove an indexed repository")]
    Task<string> RemoveCodeRepo(string repoName);
}
```

MCP tools call the API endpoints (same pattern as existing `AIMemoryTools.cs`).

---

## Implementation Order

### Phase 1 — Foundation (Steps 1-4)
1. Create `AIMemory.CodeIndex` project, add to solution
2. Add entity models to `AIMemory.Models`
3. Add DbContext configuration and migration to `AIMemory.Data`
4. Add repository interface and implementation

### Phase 2 — C# Parser (Step 5b)
5. Implement Roslyn-based C# parser (the most important language for this project)
6. Unit tests for C# parser

### Phase 3 — Indexing Engine (Step 6)
7. Implement `CodeIndexingService` with local folder support
8. Security filtering and `.gitignore` support
9. Unit tests for indexing

### Phase 4 — Query Service (Step 7)
10. Implement `CodeQueryService`
11. Byte-offset symbol retrieval
12. Unit tests for queries

### Phase 5 — API Endpoints (Step 8)
13. Add all `/api/code/` endpoints to `Program.cs`
14. Integration tests

### Phase 6 — MCP Tools (Step 9)
15. Add `CodeTools.cs` to MCP server
16. End-to-end testing with Claude

### Phase 7 — Additional Parsers (Step 5c)
17. Python parser
18. JavaScript/TypeScript parser
19. Go parser
20. Additional languages as needed

---

## File Changes Summary

### New Files
| File | Description |
|------|-------------|
| `src/AIMemory.CodeIndex/AIMemory.CodeIndex.csproj` | New class library project |
| `src/AIMemory.CodeIndex/CodeIndexingService.cs` | Indexing engine |
| `src/AIMemory.CodeIndex/CodeQueryService.cs` | Query/retrieval service |
| `src/AIMemory.CodeIndex/Parsers/ILanguageParser.cs` | Parser interface + ParsedSymbol |
| `src/AIMemory.CodeIndex/Parsers/ParserRegistry.cs` | Extension-to-parser mapping |
| `src/AIMemory.CodeIndex/Parsers/CSharpParser.cs` | Roslyn-based C# parser |
| `src/AIMemory.CodeIndex/Parsers/PythonParser.cs` | Regex-based Python parser |
| `src/AIMemory.CodeIndex/Parsers/TypeScriptParser.cs` | JS/TS parser |
| `src/AIMemory.CodeIndex/Parsers/GoParser.cs` | Go parser |
| `src/AIMemory.CodeIndex/Security/FileFilter.cs` | Security filtering, gitignore |
| `src/AIMemory.Models/Entities/CodeRepository.cs` | Entity |
| `src/AIMemory.Models/Entities/CodeFile.cs` | Entity |
| `src/AIMemory.Models/Entities/CodeSymbol.cs` | Entity |
| `src/AIMemory.Models/Dtos/CodeDtos.cs` | Request/response DTOs |
| `src/AIMemory.Data/Repositories/ICodeRepository.cs` | Repository interface |
| `src/AIMemory.Data/Repositories/CodeRepository.cs` | Repository implementation |
| `src/AIMemory.Mcp/Tools/CodeTools.cs` | MCP tool definitions |
| `tests/AIMemory.CodeIndex.Tests/` | Unit tests |

### Modified Files
| File | Change |
|------|--------|
| `src/AIMemory.slnx` | Add AIMemory.CodeIndex project |
| `src/AIMemory.Data/AIMemoryDbContext.cs` | Add DbSets + OnModelCreating config |
| `src/AIMemory.Api/Program.cs` | Add `/api/code/*` endpoints + DI registration |
| `src/AIMemory.Api/AIMemory.Api.csproj` | Add project reference to AIMemory.CodeIndex |
| `src/AIMemory.Mcp/AIMemory.Mcp.csproj` | Add project reference to AIMemory.CodeIndex |
| `src/AIMemory.Mcp/Program.cs` | Register HttpClient for CodeTools |

---

## Open Questions / Decisions Needed

1. **Tree-sitter vs Roslyn+Regex:** Tree-sitter gives accurate AST for all languages but C# bindings may be immature. Roslyn is perfect for C# but doesn't help with other languages. **Recommendation:** Start with Roslyn for C# + regex for others. Swap to tree-sitter later if good bindings emerge.

2. **GitHub indexing:** Clone the repo locally then index, or use GitHub API? Cloning is simpler and works offline. **Recommendation:** Clone via `git clone --depth 1` into a temp/cache directory.

3. **Source code storage:** Store full source in DB or read from disk at query time? jCodeMunch reads from disk via byte offsets. **Recommendation:** Read from disk (the indexed path must remain accessible). Store only metadata in DB.

4. **LLM-generated summaries:** jCodeMunch optionally generates one-line summaries per symbol via Claude/Gemini. Should we support this? **Recommendation:** Add `Summary` column but leave it nullable for Phase 1. Add LLM summarization as a future enhancement.

5. **UI integration:** Should the React frontend have a code browser? **Recommendation:** Not in this plan. Focus on API + MCP first. UI can be added later.
