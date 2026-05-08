using Microsoft.Extensions.Logging;
using AIMemory.Data.Repositories;
using AIMemory.Identity;
using AIMemory.Models.Dtos;
using AIMemory.Models.Entities;

namespace AIMemory.CodeIndex;

/// <summary>
/// Read-side queries over the code index. Post-phase-6, queries are scoped by project_id
/// (a 64-character lowercase hex SHA-256) plus the local host's id (so we look up file
/// locations on the right machine).
/// </summary>
public class CodeQueryService
{
    private readonly ICodeIndexRepository _repo;
    private readonly IHostIdProvider _hostIds;
    private readonly ILogger<CodeQueryService> _logger;

    public CodeQueryService(ICodeIndexRepository repo, IHostIdProvider hostIds, ILogger<CodeQueryService> logger)
    {
        _repo = repo;
        _hostIds = hostIds;
        _logger = logger;
    }

    public async Task<List<CodeRepoResponse>> ListRepositoriesAsync()
    {
        var projects = await _repo.ListProjectsAsync();
        return projects.Select(ToResponse).ToList();
    }

    public async Task<FileTreeResponse?> GetFileTreeAsync(string projectId)
    {
        var project = await _repo.GetProjectAsync(projectId);
        if (project == null) return null;

        var hostId = _hostIds.GetHostId();
        var locations = await _repo.GetFileTreeAsync(projectId, hostId);
        return new FileTreeResponse
        {
            ProjectId = projectId,
            Name = project.DisplayName,
            Files = locations.Select(l => new FileTreeNode
            {
                Path = l.RelPath,
                Language = l.Language,
                FileSize = l.FileSize,
                SymbolCount = 0
            }).ToList()
        };
    }

    public async Task<FileOutlineResponse?> GetFileOutlineAsync(string projectId, string filePath)
    {
        var project = await _repo.GetProjectAsync(projectId);
        if (project == null) return null;

        var hostId = _hostIds.GetHostId();
        var symbols = await _repo.GetFileOutlineAsync(projectId, hostId, filePath);

        // Look up language from the file_location for this host's view of the path. The
        // outline returns symbols by content; multiple paths can share content, so we go
        // through GetFileTreeAsync filtered to the path.
        var tree = await _repo.GetFileTreeAsync(projectId, hostId);
        var language = tree.FirstOrDefault(l => l.RelPath == filePath)?.Language ?? "unknown";

        return new FileOutlineResponse
        {
            ProjectId = projectId,
            FilePath = filePath,
            Language = language,
            Symbols = symbols.Select(ToOutline).ToList()
        };
    }

    public async Task<SymbolResponse?> GetSymbolAsync(string projectId, string symbolKey)
    {
        var project = await _repo.GetProjectAsync(projectId);
        if (project == null) return null;

        var symbol = await _repo.GetSymbolByKeyAsync(projectId, symbolKey);
        if (symbol == null) return null;

        var hostId = _hostIds.GetHostId();
        var relPath = await _repo.ResolveSymbolRelPathAsync(projectId, symbolKey, hostId);
        var sourceCode = await ReadSourceCodeAsync(project.SourcePath, relPath, symbol);
        return ToSymbolResponse(symbol, relPath ?? string.Empty, sourceCode);
    }

    public async Task<List<SymbolResponse>> GetSymbolsAsync(string projectId, List<string> symbolKeys)
    {
        var project = await _repo.GetProjectAsync(projectId);
        if (project == null) return [];

        var symbols = await _repo.GetSymbolsByKeysAsync(projectId, symbolKeys);
        var hostId = _hostIds.GetHostId();
        var results = new List<SymbolResponse>();

        foreach (var symbol in symbols)
        {
            var relPath = await _repo.ResolveSymbolRelPathAsync(projectId, symbol.SymbolKey, hostId);
            var sourceCode = await ReadSourceCodeAsync(project.SourcePath, relPath, symbol);
            results.Add(ToSymbolResponse(symbol, relPath ?? string.Empty, sourceCode));
        }

        return results;
    }

    public async Task<List<SymbolMatch>> SearchSymbolsAsync(string query, string? projectId, string? kind, int limit)
    {
        var symbols = await _repo.SearchSymbolsAsync(query, projectId, kind, limit);
        var projectIds = symbols.Select(s => s.ProjectId).Distinct().ToList();
        var projects = new Dictionary<string, Project>();
        foreach (var id in projectIds)
        {
            var p = await _repo.GetProjectAsync(id);
            if (p != null) projects[id] = p;
        }

        var hostId = _hostIds.GetHostId();
        var results = new List<SymbolMatch>(symbols.Count);
        foreach (var s in symbols)
        {
            var filePath = await _repo.ResolveSymbolRelPathAsync(s.ProjectId, s.SymbolKey, hostId)
                          ?? string.Empty;
            results.Add(new SymbolMatch
            {
                SymbolKey = s.SymbolKey,
                Name = s.Name,
                QualifiedName = s.QualifiedName,
                Kind = s.Kind,
                Signature = s.Signature,
                FilePath = filePath,
                RepositoryName = projects.TryGetValue(s.ProjectId, out var pp) ? pp.DisplayName : "unknown"
            });
        }
        return results;
    }

    public async Task<RepoOutlineResponse?> GetRepoOutlineAsync(string projectId)
    {
        var project = await _repo.GetProjectAsync(projectId);
        if (project == null) return null;

        var hostId = _hostIds.GetHostId();
        var locations = await _repo.GetFileTreeAsync(projectId, hostId);

        var languageGroups = locations.GroupBy(l => l.Language).Select(g => new LanguageSummary
        {
            Language = g.Key,
            FileCount = g.Count()
        }).OrderByDescending(l => l.FileCount).ToList();

        var topDirs = locations
            .Select(l => l.RelPath.Split('/').FirstOrDefault() ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var keySymbols = await _repo.SearchSymbolsAsync("", projectId, null, 50);
        var filtered = keySymbols
            .Where(s => s.Kind is "class" or "interface" or "struct" or "record" or "enum")
            .Select(ToOutline)
            .Take(30)
            .ToList();

        return new RepoOutlineResponse
        {
            ProjectId = projectId,
            Name = project.DisplayName,
            FileCount = project.FileCount,
            SymbolCount = project.SymbolCount,
            Languages = languageGroups,
            TopLevelDirectories = topDirs,
            KeySymbols = filtered
        };
    }

    public async Task<List<TextMatch>> SearchTextAsync(string query, string? projectId, string? filePath, int limit)
    {
        if (string.IsNullOrEmpty(projectId)) return [];

        var project = await _repo.GetProjectAsync(projectId);
        if (project == null || !Directory.Exists(project.SourcePath)) return [];

        var hostId = _hostIds.GetHostId();
        var locations = await _repo.GetFileTreeAsync(projectId, hostId);
        if (!string.IsNullOrEmpty(filePath))
            locations = locations.Where(l => l.RelPath == filePath).ToList();

        var matches = new List<TextMatch>();

        foreach (var location in locations)
        {
            if (matches.Count >= limit) break;

            var fullPath = Path.Combine(project.SourcePath,
                location.RelPath.Replace('/', Path.DirectorySeparatorChar));
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
                            FilePath = location.RelPath,
                            LineNumber = i + 1,
                            LineContent = lines[i].TrimStart(),
                            RepositoryName = project.DisplayName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to search file: {File}", location.RelPath);
            }
        }

        return matches;
    }

    private static async Task<string> ReadSourceCodeAsync(string projectSourcePath, string? relPath, CodeSymbol symbol)
    {
        if (string.IsNullOrEmpty(relPath))
            return "[file path not resolved on this host]";

        var fullPath = Path.Combine(projectSourcePath, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return "[source file not found]";

        try
        {
            var content = await File.ReadAllTextAsync(fullPath);

            if (symbol.StartByte >= 0 && symbol.EndByte > symbol.StartByte && symbol.EndByte <= content.Length)
                return content[(int)symbol.StartByte..(int)symbol.EndByte];

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

    private static SymbolResponse ToSymbolResponse(CodeSymbol s, string relPath, string sourceCode) => new()
    {
        SymbolKey = s.SymbolKey,
        Name = s.Name,
        QualifiedName = s.QualifiedName,
        Kind = s.Kind,
        Signature = s.Signature,
        Summary = s.Summary,
        FilePath = relPath,
        StartLine = s.StartLine,
        EndLine = s.EndLine,
        SourceCode = sourceCode
    };

    private static CodeRepoResponse ToResponse(Project p) => new()
    {
        ProjectId = p.ProjectId,
        Name = p.DisplayName,
        SourceType = p.SourceType,
        SourcePath = p.SourcePath,
        FileCount = p.FileCount,
        SymbolCount = p.SymbolCount,
        IndexedAt = p.FirstSeenAt,
        UpdatedAt = p.LastSeenAt
    };
}
