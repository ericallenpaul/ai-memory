using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Data.Repositories;
using AIMemory.Models.Entities;

namespace AIMemory.CodeIndex;

public class CodeIndexingService
{
    private readonly ICodeIndexRepository _repo;
    private readonly ParserRegistry _parsers;
    private readonly FileFilter _fileFilter;
    private readonly ILogger<CodeIndexingService> _logger;

    public CodeIndexingService(
        ICodeIndexRepository repo,
        ParserRegistry parsers,
        FileFilter fileFilter,
        ILogger<CodeIndexingService> logger)
    {
        _repo = repo;
        _parsers = parsers;
        _fileFilter = fileFilter;
        _logger = logger;
    }

    public async Task<CodeRepository> IndexLocalFolderAsync(string path, string? name, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        var repoName = name ?? Path.GetFileName(fullPath);

        var codeRepo = await _repo.UpsertRepositoryAsync(new CodeRepository
        {
            Name = repoName,
            SourceType = "local",
            SourcePath = fullPath
        });

        _logger.LogInformation("Indexing local folder: {Path} as {Name}", fullPath, repoName);

        await IndexDirectoryAsync(codeRepo, fullPath, ct);

        return codeRepo;
    }

    public async Task<CodeRepository> ReindexAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var codeRepo = await _repo.GetRepositoryAsync(repositoryId)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        if (!Directory.Exists(codeRepo.SourcePath))
            throw new DirectoryNotFoundException($"Source path no longer exists: {codeRepo.SourcePath}");

        _logger.LogInformation("Re-indexing repository: {Name}", codeRepo.Name);

        await IndexDirectoryAsync(codeRepo, codeRepo.SourcePath, ct);

        return codeRepo;
    }

    private async Task IndexDirectoryAsync(CodeRepository codeRepo, string rootPath, CancellationToken ct)
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

                var codeFile = new CodeFile
                {
                    RepositoryId = codeRepo.RepositoryId,
                    FilePath = relativePath,
                    Language = _parsers.GetLanguage(filePath),
                    FileSize = fileInfo.Length,
                    ContentHash = contentHash
                };

                await _repo.UpsertFileAsync(codeFile);

                // Parse symbols
                var parser = _parsers.GetParser(filePath);
                if (parser != null)
                {
                    var parsed = parser.Parse(relativePath, content);
                    var symbols = parsed.Select(p => new CodeSymbol
                    {
                        SymbolKey = $"{relativePath}::{p.QualifiedName}#{p.Kind}",
                        Name = p.Name,
                        QualifiedName = p.QualifiedName,
                        Kind = p.Kind,
                        Signature = p.Signature,
                        StartLine = p.StartLine,
                        EndLine = p.EndLine,
                        StartByte = p.StartByte,
                        EndByte = p.EndByte,
                        ParentSymbolKey = p.ParentName != null
                            ? $"{relativePath}::{p.ParentName}#{GetParentKind(parsed, p.ParentName)}"
                            : null
                    }).ToList();

                    await _repo.UpsertSymbolsAsync(codeFile.FileId, codeRepo.RepositoryId, symbols);
                    totalSymbols += symbols.Count;

                    _logger.LogDebug("Indexed {SymbolCount} symbols from {File}", symbols.Count, relativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index file: {File}", relativePath);
            }
        }

        // Clean up files that no longer exist
        await _repo.DeleteStaleFilesAsync(codeRepo.RepositoryId, indexedPaths);

        // Update stats
        await _repo.UpdateRepositoryStatsAsync(codeRepo.RepositoryId);

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
