using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Data.Repositories;
using AIMemory.Identity;
using AIMemory.Models.Entities;

namespace AIMemory.CodeIndex;

/// <summary>
/// Indexes a local folder into the code index. Post-phase-6 this writes to the new
/// project + file_location + content-addressed code_files schema.
/// </summary>
public class CodeIndexingService
{
    private readonly ICodeIndexRepository _repo;
    private readonly ParserRegistry _parsers;
    private readonly FileFilter _fileFilter;
    private readonly IProjectIdResolver _projectIds;
    private readonly IHostIdProvider _hostIds;
    private readonly ILogger<CodeIndexingService> _logger;

    public CodeIndexingService(
        ICodeIndexRepository repo,
        ParserRegistry parsers,
        FileFilter fileFilter,
        IProjectIdResolver projectIds,
        IHostIdProvider hostIds,
        ILogger<CodeIndexingService> logger)
    {
        _repo = repo;
        _parsers = parsers;
        _fileFilter = fileFilter;
        _projectIds = projectIds;
        _hostIds = hostIds;
        _logger = logger;
    }

    public async Task<Project> IndexLocalFolderAsync(string path, string? name, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        var hostId = _hostIds.GetHostId();
        var identity = _projectIds.Resolve(fullPath, hostId);
        var displayName = name ?? Path.GetFileName(fullPath);

        var project = await _repo.UpsertProjectAsync(new Project
        {
            ProjectId = identity.ProjectId,
            DisplayName = displayName,
            CanonicalRemoteUrl = identity.CanonicalRemoteUrl,
            RootCommitSha = identity.RootCommitSha,
            IdentityKind = identity.IdentityKind,
            SourceType = "local",
            SourcePath = fullPath
        });

        _logger.LogInformation("Indexing local folder: {Path} as {Name} (project_id={Id}, kind={Kind})",
            fullPath, displayName, identity.ProjectId, identity.IdentityKind);

        await IndexDirectoryAsync(project, hostId, fullPath, ct);
        return project;
    }

    public async Task<Project> ReindexAsync(string projectId, CancellationToken ct = default)
    {
        var project = await _repo.GetProjectAsync(projectId)
            ?? throw new InvalidOperationException($"Project {projectId} not found");

        if (!Directory.Exists(project.SourcePath))
            throw new DirectoryNotFoundException($"Source path no longer exists: {project.SourcePath}");

        _logger.LogInformation("Re-indexing project: {Name}", project.DisplayName);

        var hostId = _hostIds.GetHostId();
        await IndexDirectoryAsync(project, hostId, project.SourcePath, ct);
        return project;
    }

    private async Task IndexDirectoryAsync(Project project, string hostId, string rootPath, CancellationToken ct)
    {
        var files = _fileFilter.EnumerateFiles(rootPath)
            .Where(f => _parsers.IsSupported(f) && !_fileFilter.IsBinaryFile(f))
            .ToList();

        _logger.LogInformation("Found {Count} parseable files in {Path}", files.Count, rootPath);

        var indexedPaths = new List<string>();
        int totalSymbols = 0;

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = _fileFilter.GetRelativePath(filePath, rootPath);
            indexedPaths.Add(relativePath);

            try
            {
                var content = await File.ReadAllTextAsync(filePath, ct);
                var contentHash = ComputeHash(content);
                var fileInfo = new FileInfo(filePath);
                var language = _parsers.GetLanguage(filePath);

                await _repo.UpsertFileAsync(
                    hostId, project.ProjectId, relativePath, language, fileInfo.Length, contentHash);

                var parser = _parsers.GetParser(filePath);
                if (parser != null)
                {
                    var parsed = parser.Parse(relativePath, content);
                    var symbols = parsed.Select(p => new CodeSymbol
                    {
                        // Phase 6 symbol_key format: project_id::QualifiedName#kind.
                        SymbolKey = $"{project.ProjectId}::{p.QualifiedName}#{p.Kind}",
                        Name = p.Name,
                        QualifiedName = p.QualifiedName,
                        Kind = p.Kind,
                        Signature = p.Signature,
                        StartLine = p.StartLine,
                        EndLine = p.EndLine,
                        StartByte = p.StartByte,
                        EndByte = p.EndByte,
                        ParentSymbolKey = p.ParentName != null
                            ? $"{project.ProjectId}::{p.ParentName}#{GetParentKind(parsed, p.ParentName)}"
                            : null
                    }).ToList();

                    await _repo.UpsertSymbolsAsync(contentHash, project.ProjectId, symbols);
                    totalSymbols += symbols.Count;

                    _logger.LogDebug("Indexed {SymbolCount} symbols from {File}", symbols.Count, relativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index file: {File}", relativePath);
            }
        }

        // Clean up locations that no longer exist on this host.
        await _repo.DeleteStaleFilesAsync(hostId, project.ProjectId, indexedPaths);
        await _repo.UpdateProjectStatsAsync(project.ProjectId, hostId);

        _logger.LogInformation("Indexing complete: {FileCount} files, {SymbolCount} symbols",
            indexedPaths.Count, totalSymbols);
    }

    private static string GetParentKind(List<ParsedSymbol> symbols, string parentName)
    {
        var parent = symbols.FirstOrDefault(s => s.QualifiedName == parentName);
        return parent?.Kind ?? "class";
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
