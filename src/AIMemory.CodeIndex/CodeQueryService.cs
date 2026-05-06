using Microsoft.Extensions.Logging;
using AIMemory.Data.Repositories;
using AIMemory.Models.Dtos;
using AIMemory.Models.Entities;

namespace AIMemory.CodeIndex;

public class CodeQueryService
{
    private readonly ICodeIndexRepository _repo;
    private readonly ILogger<CodeQueryService> _logger;

    public CodeQueryService(ICodeIndexRepository repo, ILogger<CodeQueryService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<List<CodeRepoResponse>> ListRepositoriesAsync()
    {
        var repos = await _repo.ListRepositoriesAsync();
        return repos.Select(r => new CodeRepoResponse
        {
            RepositoryId = r.RepositoryId,
            Name = r.Name,
            SourceType = r.SourceType,
            SourcePath = r.SourcePath,
            FileCount = r.FileCount,
            SymbolCount = r.SymbolCount,
            IndexedAt = r.IndexedAt,
            UpdatedAt = r.UpdatedAt
        }).ToList();
    }

    public async Task<FileTreeResponse?> GetFileTreeAsync(Guid repoId)
    {
        var repo = await _repo.GetRepositoryAsync(repoId);
        if (repo == null) return null;

        var files = await _repo.GetFileTreeAsync(repoId);
        return new FileTreeResponse
        {
            RepositoryId = repoId,
            Name = repo.Name,
            Files = files.Select(f => new FileTreeNode
            {
                Path = f.FilePath,
                Language = f.Language,
                FileSize = f.FileSize,
                SymbolCount = 0 // Populated below
            }).ToList()
        };
    }

    public async Task<FileOutlineResponse?> GetFileOutlineAsync(Guid repoId, string filePath)
    {
        var repo = await _repo.GetRepositoryAsync(repoId);
        if (repo == null) return null;

        var symbols = await _repo.GetFileOutlineAsync(repoId, filePath);
        return new FileOutlineResponse
        {
            RepositoryId = repoId,
            FilePath = filePath,
            Language = symbols.FirstOrDefault()?.File?.Language ?? "unknown",
            Symbols = symbols.Select(ToOutline).ToList()
        };
    }

    public async Task<SymbolResponse?> GetSymbolAsync(Guid repoId, string symbolKey)
    {
        var repo = await _repo.GetRepositoryAsync(repoId);
        if (repo == null) return null;

        var symbol = await _repo.GetSymbolByKeyAsync(repoId, symbolKey);
        if (symbol == null) return null;

        var sourceCode = await ReadSourceCodeAsync(repo.SourcePath, symbol);
        return ToSymbolResponse(symbol, sourceCode);
    }

    public async Task<List<SymbolResponse>> GetSymbolsAsync(Guid repoId, List<string> symbolKeys)
    {
        var repo = await _repo.GetRepositoryAsync(repoId);
        if (repo == null) return [];

        var symbols = await _repo.GetSymbolsByKeysAsync(repoId, symbolKeys);
        var results = new List<SymbolResponse>();

        foreach (var symbol in symbols)
        {
            var sourceCode = await ReadSourceCodeAsync(repo.SourcePath, symbol);
            results.Add(ToSymbolResponse(symbol, sourceCode));
        }

        return results;
    }

    public async Task<List<SymbolMatch>> SearchSymbolsAsync(string query, Guid? repoId, string? kind, int limit)
    {
        var symbols = await _repo.SearchSymbolsAsync(query, repoId, kind, limit);

        // We need repo names — batch lookup
        var repoIds = symbols.Select(s => s.RepositoryId).Distinct().ToList();
        var repos = new Dictionary<Guid, string>();
        foreach (var id in repoIds)
        {
            var repo = await _repo.GetRepositoryAsync(id);
            if (repo != null) repos[id] = repo.Name;
        }

        return symbols.Select(s =>
        {
            var filePath = s.SymbolKey.Split("::")[0];
            return new SymbolMatch
            {
                SymbolKey = s.SymbolKey,
                Name = s.Name,
                QualifiedName = s.QualifiedName,
                Kind = s.Kind,
                Signature = s.Signature,
                FilePath = filePath,
                RepositoryName = repos.GetValueOrDefault(s.RepositoryId, "unknown")
            };
        }).ToList();
    }

    public async Task<RepoOutlineResponse?> GetRepoOutlineAsync(Guid repoId)
    {
        var repo = await _repo.GetRepositoryAsync(repoId);
        if (repo == null) return null;

        var files = await _repo.GetFileTreeAsync(repoId);

        var languageGroups = files.GroupBy(f => f.Language).Select(g => new LanguageSummary
        {
            Language = g.Key,
            FileCount = g.Count()
        }).OrderByDescending(l => l.FileCount).ToList();

        var topDirs = files
            .Select(f => f.FilePath.Split('/').FirstOrDefault() ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // Get key symbols: classes and top-level functions
        var keySymbols = await _repo.SearchSymbolsAsync("", repoId, null, 50);
        var filtered = keySymbols
            .Where(s => s.Kind is "class" or "interface" or "struct" or "record" or "enum")
            .Select(ToOutline)
            .Take(30)
            .ToList();

        return new RepoOutlineResponse
        {
            RepositoryId = repoId,
            Name = repo.Name,
            FileCount = repo.FileCount,
            SymbolCount = repo.SymbolCount,
            Languages = languageGroups,
            TopLevelDirectories = topDirs,
            KeySymbols = filtered
        };
    }

    public async Task<List<TextMatch>> SearchTextAsync(string query, Guid? repoId, string? filePath, int limit)
    {
        if (repoId == null) return [];

        var repo = await _repo.GetRepositoryAsync(repoId.Value);
        if (repo == null || !Directory.Exists(repo.SourcePath)) return [];

        var files = await _repo.GetFileTreeAsync(repoId.Value);
        if (!string.IsNullOrEmpty(filePath))
            files = files.Where(f => f.FilePath == filePath).ToList();

        var matches = new List<TextMatch>();

        foreach (var file in files)
        {
            if (matches.Count >= limit) break;

            var fullPath = Path.Combine(repo.SourcePath, file.FilePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(fullPath);
                for (int i = 0; i < lines.Length && matches.Count < limit; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new TextMatch
                        {
                            FilePath = file.FilePath,
                            LineNumber = i + 1,
                            LineContent = lines[i].TrimStart(),
                            RepositoryName = repo.Name
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to search file: {File}", file.FilePath);
            }
        }

        return matches;
    }

    private static async Task<string> ReadSourceCodeAsync(string repoSourcePath, CodeSymbol symbol)
    {
        var filePath = symbol.SymbolKey.Split("::")[0];
        var fullPath = Path.Combine(repoSourcePath, filePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
            return "[source file not found]";

        try
        {
            var content = await File.ReadAllTextAsync(fullPath);

            // Use byte offsets for precise extraction
            if (symbol.StartByte >= 0 && symbol.EndByte > symbol.StartByte && symbol.EndByte <= content.Length)
            {
                return content[(int)symbol.StartByte..(int)symbol.EndByte];
            }

            // Fall back to line-based extraction
            var lines = content.Split('\n');
            var start = Math.Max(0, symbol.StartLine - 1);
            var end = Math.Min(lines.Length, symbol.EndLine);
            return string.Join('\n', lines[start..end]);
        }
        catch
        {
            return "[failed to read source]";
        }
    }

    private static SymbolOutline ToOutline(CodeSymbol s) => new()
    {
        SymbolKey = s.SymbolKey,
        Name = s.Name,
        QualifiedName = s.QualifiedName,
        Kind = s.Kind,
        Signature = s.Signature,
        Summary = s.Summary,
        StartLine = s.StartLine,
        EndLine = s.EndLine,
        ParentSymbolKey = s.ParentSymbolKey
    };

    private static SymbolResponse ToSymbolResponse(CodeSymbol s, string sourceCode)
    {
        var filePath = s.SymbolKey.Split("::")[0];
        return new SymbolResponse
        {
            SymbolKey = s.SymbolKey,
            Name = s.Name,
            QualifiedName = s.QualifiedName,
            Kind = s.Kind,
            Signature = s.Signature,
            Summary = s.Summary,
            FilePath = filePath,
            StartLine = s.StartLine,
            EndLine = s.EndLine,
            SourceCode = sourceCode
        };
    }
}
