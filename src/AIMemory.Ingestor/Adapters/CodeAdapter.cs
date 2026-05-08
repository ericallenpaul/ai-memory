using System.Collections.Concurrent;
using System.Text.Json;
using AIMemory.CodeIndex.Git;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Hashing;
using AIMemory.Models.Dtos;
using AIMemory.Models.Events;

namespace AIMemory.Ingestor.Adapters;

public class CodeAdapter : ISourceAdapter
{
    private readonly FileFilter _fileFilter;
    private readonly ParserRegistry _parserRegistry;
    private readonly GitChangeDetector _gitDetector;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IFileHasher _fileHasher;
    private readonly ILogger<CodeAdapter> _logger;
    private readonly ConcurrentDictionary<string, (string Content, string Hash, FileInfo Info)> _fileCache = new();
    private readonly ConcurrentDictionary<string, string> _fileToWatchPath = new();

    // Tracks which watch path was used in git mode this cycle, so Reconcile knows
    // where to compute deletions and update the watch-path checkpoint.
    private readonly ConcurrentDictionary<string, GitWatchState> _pendingGitState = new();

    public string SourceName => "code-index";

    public CodeAdapter(
        FileFilter fileFilter,
        ParserRegistry parserRegistry,
        GitChangeDetector gitDetector,
        ICheckpointStore checkpointStore,
        ILogger<CodeAdapter> logger,
        IFileHasher? fileHasher = null)
    {
        _fileFilter = fileFilter;
        _parserRegistry = parserRegistry;
        _gitDetector = gitDetector;
        _checkpointStore = checkpointStore;
        _logger = logger;
        // Default to the streaming hasher when DI doesn't provide one. Constructor argument
        // is optional so existing tests that hand-build the adapter don't have to be edited.
        _fileHasher = fileHasher ?? new StreamingFileHasher();
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

            var isGit = _gitDetector.IsGitRepository(watchPath);

            // GitOnly + non-git path: skip entirely with a warning. The user explicitly
            // opted out of FS fallback for this watch path.
            if (config.DetectionMode == ChangeDetectionMode.GitOnly && !isGit)
            {
                _logger.LogWarning(
                    "Watch path {Path} is not a git repository and DetectionMode=GitOnly — skipping",
                    watchPath);
                continue;
            }

            var useGit = config.DetectionMode != ChangeDetectionMode.FsOnly && isGit;

            if (useGit)
            {
                foreach (var file in EnumerateGitFiles(watchPath))
                    yield return file;
            }
            else
            {
                foreach (var file in EnumerateFsFiles(watchPath))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// Used by Reconcile to decide which deletion-detection path to run. Mirrors the logic
    /// in <see cref="DiscoverFiles"/>: GitOnly+non-git is treated as "this path was skipped"
    /// so reconcile also no-ops for it.
    /// </summary>
    private bool ShouldUseGit(ChangeDetectionMode mode, string watchPath)
    {
        var isGit = _gitDetector.IsGitRepository(watchPath);
        if (mode == ChangeDetectionMode.GitOnly && !isGit) return false; // and watch-path is skipped
        return mode != ChangeDetectionMode.FsOnly && isGit;
    }

    private IEnumerable<string> EnumerateGitFiles(string watchPath)
    {
        var checkpointKey = CheckpointKey.ForWatchPath(watchPath);
        var lastCheckpoint = _checkpointStore.GetCheckpoint(checkpointKey);
        var lastState = lastCheckpoint?.GitHeadSha != null
            ? new GitWatchState(
                lastCheckpoint.GitHeadSha,
                (lastCheckpoint.GitDirtyPaths ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase))
            : GitWatchState.Empty;

        // Capture state and candidate set up-front. If anything throws, fall back to
        // the FS walk — but that fallback can't live in a try/catch around a yield.
        var (currentState, candidates) = TryCaptureGitCandidates(watchPath, lastState);
        if (currentState == null)
        {
            foreach (var file in EnumerateFsFiles(watchPath))
                yield return file;
            yield break;
        }

        _pendingGitState[watchPath] = currentState;

        foreach (var absPath in candidates!)
        {
            // Apply the same filters as filesystem mode so we don't yield .git internals,
            // node_modules, binary files, or unsupported extensions.
            if (!File.Exists(absPath)) continue;
            if (!_parserRegistry.IsSupported(absPath)) continue;
            if (_fileFilter.IsPathExcluded(absPath, watchPath)) continue;
            if (_fileFilter.IsBinaryFile(absPath)) continue;

            _fileToWatchPath[absPath] = watchPath;
            yield return absPath;
        }
    }

    private (GitWatchState? state, IEnumerable<string>? candidates) TryCaptureGitCandidates(
        string watchPath, GitWatchState lastState)
    {
        try
        {
            var state = _gitDetector.GetCurrentState(watchPath);
            // Materialize the candidate list now so any libgit2 errors surface here, not
            // mid-iteration in the caller. A list per scan is fine — the count is bounded
            // by changed files, not repo size.
            var candidates = string.IsNullOrEmpty(lastState.HeadSha)
                ? _gitDetector.EnumerateAllFiles(watchPath).ToList()
                : _gitDetector.ChangedSince(watchPath, lastState).ToList();
            return (state, candidates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git enumeration failed for {Path} — falling back to filesystem walk", watchPath);
            return (null, null);
        }
    }

    private IEnumerable<string> EnumerateFsFiles(string watchPath)
    {
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

        // Tier 2: content hash check — read file's raw bytes once, derive hash + decoded text.
        // Hash is computed over the actual file bytes (per design doc §2.3) rather than UTF-8
        // round-tripped text, so a file with a UTF-8 BOM or non-ASCII bytes hashes as itself.
        byte[] rawBytes;
        try
        {
            rawBytes = File.ReadAllBytes(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file: {Path}", filePath);
            yield break;
        }

        var hash = _fileHasher.HashBytes(rawBytes);
        // Decode for the parser. UTF-8 with BOM detection matches what File.ReadAllText did
        // before, so symbol parsing is unaffected.
        var content = System.Text.Encoding.UTF8.GetString(StripUtf8Bom(rawBytes));

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

        // Emit CodeFileUpsert event. RelPath is the v2 field (per design doc §3.3); FilePath
        // is kept populated for backcompat with v1 primaries during the migration window.
        var fileEvent = new CodeFileUpsertEvent
        {
            RepositoryName = repoName,
            SourceType = "local",
            SourcePath = watchPath,
            FilePath = relativePath,
            RelPath = relativePath,
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

    /// <summary>
    /// End-of-cycle hook. Two responsibilities:
    /// <list type="number">
    ///   <item>Emit <see cref="CodeFileDeleteEvent"/>s for files that disappeared since last scan.
    ///         Git mode uses libgit2 diff; FS mode compares against the last enumerated set.</item>
    ///   <item>Persist the per-watchpath checkpoint (HEAD sha, dirty paths, last enumeration)
    ///         so the next cycle has a baseline to diff against.</item>
    /// </list>
    /// </summary>
    public IEnumerable<IngestEvent> Reconcile(
        SourceConfig config,
        IReadOnlyDictionary<string, IReadOnlyList<string>> enumeratedFilesByWatchPath)
    {
        foreach (var watchPath in config.WatchPaths)
        {
            if (!Directory.Exists(watchPath)) continue;

            var enumerated = enumeratedFilesByWatchPath.TryGetValue(watchPath, out var list)
                ? list
                : Array.Empty<string>();

            var repoName = Path.GetFileName(watchPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var checkpointKey = CheckpointKey.ForWatchPath(watchPath);
            var lastCheckpoint = _checkpointStore.GetCheckpoint(checkpointKey);
            var useGit = ShouldUseGit(config.DetectionMode, watchPath);

            var deletedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (useGit)
            {
                // Git mode: ask libgit2 for deletions between last-seen HEAD and now.
                if (lastCheckpoint?.GitHeadSha != null)
                {
                    var lastState = new GitWatchState(
                        lastCheckpoint.GitHeadSha,
                        (lastCheckpoint.GitDirtyPaths ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));

                    try
                    {
                        foreach (var rel in _gitDetector.EnumerateDeletions(watchPath, lastState))
                            deletedRelativePaths.Add(rel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "EnumerateDeletions failed for {Path}", watchPath);
                    }
                }
            }
            else
            {
                // FS mode: filesystem is the boss. Anything in the previous enumeration that
                // isn't in the current enumeration (and doesn't exist on disk anymore) is gone.
                var previous = lastCheckpoint?.LastEnumeratedFiles ?? Array.Empty<string>();
                var currentSet = enumerated
                    .Select(abs => Path.GetRelativePath(watchPath, abs).Replace('\\', '/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in previous)
                {
                    if (!currentSet.Contains(rel))
                    {
                        var abs = Path.Combine(watchPath, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(abs))
                            deletedRelativePaths.Add(rel);
                    }
                }
            }

            foreach (var rel in deletedRelativePaths)
            {
                // Filter out files we wouldn't have indexed in the first place — no point
                // emitting a delete for, say, an unsupported extension that's now gone.
                if (!_parserRegistry.IsSupported(rel)) continue;

                var deleteEvent = new CodeFileDeleteEvent
                {
                    RepositoryName = repoName,
                    FilePath = rel,
                    RelPath = rel,
                    MachineName = Environment.MachineName
                };

                yield return new IngestEvent
                {
                    Type = "CodeFileDelete",
                    // Idempotency key includes a timestamp tick so re-emitting a delete (e.g.
                    // because the API didn't ack the first one) doesn't collide with the prior
                    // entry; the API's effect is idempotent regardless.
                    IdempotencyKey = $"code-delete|{watchPath}|{rel}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    Payload = JsonSerializer.SerializeToElement(deleteEvent)
                };
            }

            // Persist watch-path checkpoint for next cycle. Both git and FS modes record
            // the enumerated relative-path list so we can recover deletion detection if
            // git state is lost (e.g. user toggles modes).
            var newCheckpoint = new Checkpoint
            {
                LastSeenAt = DateTimeOffset.UtcNow,
                LastEnumeratedFiles = enumerated
                    .Select(abs => Path.GetRelativePath(watchPath, abs).Replace('\\', '/'))
                    .ToArray()
            };

            if (useGit && _pendingGitState.TryRemove(watchPath, out var gitState))
            {
                newCheckpoint.GitHeadSha = gitState.HeadSha;
                newCheckpoint.GitDirtyPaths = gitState.DirtyPaths.ToArray();
            }

            _checkpointStore.SaveCheckpoint(checkpointKey, newCheckpoint);
        }
    }

    /// <summary>
    /// Returns the raw bytes minus a leading UTF-8 BOM (<c>EF BB BF</c>) if present. The hash
    /// is computed over the original bytes <em>including</em> the BOM (that's what disk says),
    /// but the parser expects a BOM-free string — same shape as <see cref="File.ReadAllText(string)"/>.
    /// </summary>
    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new ReadOnlySpan<byte>(bytes, 3, bytes.Length - 3);
        return bytes;
    }
}
