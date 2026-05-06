using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Models.Dtos;
using AIMemory.Models.Events;

namespace AIMemory.Ingestor.Adapters;

public class CodeAdapter : ISourceAdapter
{
    private readonly FileFilter _fileFilter;
    private readonly ParserRegistry _parserRegistry;
    private readonly ILogger<CodeAdapter> _logger;
    private readonly ConcurrentDictionary<string, (string Content, string Hash, FileInfo Info)> _fileCache = new();
    private readonly ConcurrentDictionary<string, string> _fileToWatchPath = new();

    public string SourceName => "code-index";

    public CodeAdapter(FileFilter fileFilter, ParserRegistry parserRegistry, ILogger<CodeAdapter> logger)
    {
        _fileFilter = fileFilter;
        _parserRegistry = parserRegistry;
        _logger = logger;
    }

    public IEnumerable<string> DiscoverFiles(SourceConfig config)
    {
        foreach (var watchPath in config.WatchPaths)
        {
            if (!Directory.Exists(watchPath))
            {
                _logger.LogWarning("Watch path does not exist: {Path}", watchPath);
                continue;
            }

            foreach (var file in _fileFilter.EnumerateFiles(watchPath))
            {
                if (!_parserRegistry.IsSupported(file))
                    continue;

                if (_fileFilter.IsBinaryFile(file))
                    continue;

                _fileToWatchPath[file] = watchPath;
                yield return file;
            }
        }
    }

    public IEnumerable<RawRecord> ReadNewRecords(string filePath, Checkpoint? checkpoint)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) yield break;

        // Tier 1: mtime + size check — skip if unchanged
        if (checkpoint != null
            && checkpoint.FileMtime.HasValue
            && checkpoint.FileSize == fileInfo.Length
            && Math.Abs((fileInfo.LastWriteTimeUtc - checkpoint.FileMtime.Value).TotalSeconds) < 1)
        {
            yield break;
        }

        // Tier 2: content hash check — read file and compare hash
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file: {Path}", filePath);
            yield break;
        }

        var hash = ComputeSha256(content);

        // If mtime changed but content is identical, skip (touch-only change)
        if (checkpoint?.ContentHash != null && checkpoint.ContentHash == hash)
        {
            yield break;
        }

        // Tier 3: actual content change — cache for ParseRecord and yield
        _fileCache[filePath] = (content, hash, fileInfo);

        yield return new RawRecord
        {
            FilePath = filePath,
            Offset = 1,
            Line = "",
            Source = SourceName,
            ContentHash = hash
        };
    }

    public IEnumerable<IngestEvent> ParseRecord(RawRecord raw)
    {
        if (!_fileCache.TryRemove(raw.FilePath, out var cached))
        {
            _logger.LogWarning("No cached content for {Path} — skipping", raw.FilePath);
            yield break;
        }

        var (content, hash, fileInfo) = cached;

        // Resolve watch path → repo name and relative path
        _fileToWatchPath.TryRemove(raw.FilePath, out var watchPath);
        watchPath ??= Path.GetDirectoryName(raw.FilePath) ?? "";
        var repoName = Path.GetFileName(watchPath);
        var relativePath = Path.GetRelativePath(watchPath, raw.FilePath).Replace('\\', '/');

        var language = _parserRegistry.GetLanguage(raw.FilePath) ?? "unknown";

        // Emit CodeFileUpsert event
        var fileEvent = new CodeFileUpsertEvent
        {
            RepositoryName = repoName,
            SourceType = "local",
            SourcePath = watchPath,
            FilePath = relativePath,
            Language = language,
            FileSize = fileInfo.Length,
            ContentHash = hash,
            MachineName = Environment.MachineName
        };

        yield return new IngestEvent
        {
            Type = "CodeFileUpsert",
            IdempotencyKey = $"code|{raw.FilePath}|{hash}",
            Payload = JsonSerializer.SerializeToElement(fileEvent)
        };

        // Parse symbols
        var parser = _parserRegistry.GetParser(raw.FilePath);
        if (parser != null)
        {
            var symbols = parser.Parse(raw.FilePath, content);
            if (symbols.Count > 0)
            {
                var symbolEvent = new CodeSymbolBatchEvent
                {
                    RepositoryName = repoName,
                    FilePath = relativePath,
                    Symbols = symbols.Select(s => new CodeSymbolEvent
                    {
                        SymbolKey = $"{relativePath}::{s.QualifiedName}#{s.Kind}",
                        Name = s.Name,
                        QualifiedName = s.QualifiedName,
                        Kind = s.Kind,
                        Signature = s.Signature,
                        StartLine = s.StartLine,
                        EndLine = s.EndLine,
                        StartByte = s.StartByte,
                        EndByte = s.EndByte,
                        ParentSymbolKey = s.ParentName != null
                            ? $"{relativePath}::{s.ParentName}#class"
                            : null
                    }).ToList()
                };

                yield return new IngestEvent
                {
                    Type = "CodeSymbolBatch",
                    IdempotencyKey = $"code-symbols|{raw.FilePath}|{hash}",
                    Payload = JsonSerializer.SerializeToElement(symbolEvent)
                };
            }
        }
    }

    private static string ComputeSha256(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
