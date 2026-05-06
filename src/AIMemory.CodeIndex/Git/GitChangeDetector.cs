using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.CodeIndex.Git;

/// <summary>
/// Tier-0 change detection backed by libgit2. When a watch path is a git repository, this
/// short-circuits the per-file mtime/size walk by asking git which files actually changed
/// since the last scan. Falls through silently for non-git folders — the caller is expected
/// to use the existing filesystem walk in that case.
/// </summary>
public sealed class GitChangeDetector
{
    private readonly ILogger<GitChangeDetector> _logger;

    public GitChangeDetector(ILogger<GitChangeDetector>? logger = null)
    {
        _logger = logger ?? NullLogger<GitChangeDetector>.Instance;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is the root of a git working tree
    /// (i.e. <c>Repository.IsValid(path)</c>). Wraps the libgit2 call so callers don't
    /// have to take a direct dependency on LibGit2Sharp types.
    /// </summary>
    public bool IsGitRepository(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return false;

        try
        {
            return Repository.IsValid(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IsValid check failed for {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Captures the current head SHA + working-tree dirty/untracked path set. Persisted
    /// per-watchpath in the checkpoint so the next scan can compute a diff against it.
    /// </summary>
    public GitWatchState GetCurrentState(string repoPath)
    {
        using var repo = new Repository(repoPath);

        var head = repo.Head?.Tip?.Sha ?? string.Empty;

        // Working-tree dirty set: anything modified, added, deleted, or untracked.
        // We exclude ignored files because they're not part of the index.
        var statusOptions = new StatusOptions
        {
            IncludeIgnored = false,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        };

        var dirty = repo.RetrieveStatus(statusOptions)
            .Where(e => e.State != FileStatus.Ignored && e.State != FileStatus.Unaltered)
            .Select(e => NormalizePath(e.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new GitWatchState(head, dirty);
    }

    /// <summary>
    /// Enumerates all tracked files at HEAD plus untracked-but-not-ignored files in the
    /// working tree. Used on the first scan of a watch path before any state exists.
    /// Paths are returned as absolute paths.
    /// </summary>
    public IEnumerable<string> EnumerateAllFiles(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracked files at HEAD.
        if (repo.Head?.Tip != null)
        {
            foreach (var entry in repo.Index)
            {
                var rel = NormalizePath(entry.Path);
                if (seen.Add(rel))
                    yield return Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        // Untracked-but-not-ignored files. These haven't been `git add`-ed yet but the
        // user might still want them indexed (a freshly-created .cs file before commit).
        var statusOptions = new StatusOptions
        {
            IncludeIgnored = false,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        };

        foreach (var entry in repo.RetrieveStatus(statusOptions))
        {
            if (entry.State == FileStatus.NewInWorkdir)
            {
                var rel = NormalizePath(entry.FilePath);
                if (seen.Add(rel))
                    yield return Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
            }
        }
    }

    /// <summary>
    /// Computes the set of files that have changed (modified, added, untracked) since the
    /// last recorded state. Does NOT include deletions — see <see cref="EnumerateDeletions"/>.
    /// Paths returned as absolute paths.
    /// </summary>
    public IEnumerable<string> ChangedSince(string repoPath, GitWatchState lastState)
    {
        // ComputeChangedSet does the libgit2 work and may signal "fall back to full scan"
        // by returning null. Yielding happens here in iterator form so consumers can stream.
        var changed = ComputeChangedSet(repoPath, lastState);
        if (changed == null)
        {
            _logger.LogInformation("Recorded HEAD {Sha} not resolvable in {Repo} — falling back to full re-scan",
                lastState.HeadSha, repoPath);
            foreach (var f in EnumerateAllFiles(repoPath))
                yield return f;
            yield break;
        }

        foreach (var rel in changed)
            yield return Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private HashSet<string>? ComputeChangedSet(string repoPath, GitWatchState lastState)
    {
        using var repo = new Repository(repoPath);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Commits between lastState.HeadSha and current HEAD.
        if (!string.IsNullOrEmpty(lastState.HeadSha) && repo.Head?.Tip != null)
        {
            try
            {
                var oldCommit = repo.Lookup<Commit>(lastState.HeadSha);
                var newCommit = repo.Head.Tip;

                if (oldCommit == null) return null; // signal: fall back to full scan

                if (oldCommit.Sha != newCommit.Sha)
                {
                    var diff = repo.Diff.Compare<TreeChanges>(oldCommit.Tree, newCommit.Tree);
                    foreach (var change in diff)
                    {
                        if (change.Status == ChangeKind.Deleted) continue;
                        changed.Add(NormalizePath(change.Path));
                    }
                }
            }
            catch (NotFoundException)
            {
                // The recorded HEAD is no longer in the repo (e.g. branch was reset/rebased).
                return null;
            }
        }

        // Working-tree changes: anything currently dirty, plus anything that was dirty
        // last time but isn't now (we still want to re-scan it to confirm content).
        var statusOptions = new StatusOptions
        {
            IncludeIgnored = false,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        };

        var currentlyDirty = repo.RetrieveStatus(statusOptions)
            .Where(e => e.State != FileStatus.Ignored
                     && e.State != FileStatus.Unaltered
                     && (e.State & FileStatus.DeletedFromWorkdir) == 0
                     && (e.State & FileStatus.DeletedFromIndex) == 0)
            .Select(e => NormalizePath(e.FilePath));

        foreach (var rel in currentlyDirty)
            changed.Add(rel);

        // Files dirty last time but not now (reverted) — re-scan to confirm.
        foreach (var rel in lastState.DirtyPaths)
        {
            if (!changed.Contains(rel))
            {
                var abs = Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs))
                    changed.Add(rel);
            }
        }

        return changed;
    }

    /// <summary>
    /// Returns relative paths (forward-slash) of files that were tracked in
    /// <paramref name="lastState"/> but no longer exist in HEAD or the working tree.
    /// Used to emit <c>CodeFileDeleteEvent</c>s.
    /// </summary>
    public IEnumerable<string> EnumerateDeletions(string repoPath, GitWatchState lastState)
    {
        using var repo = new Repository(repoPath);

        if (string.IsNullOrEmpty(lastState.HeadSha) || repo.Head?.Tip == null)
            yield break;

        var oldCommit = repo.Lookup<Commit>(lastState.HeadSha);
        if (oldCommit == null)
        {
            // Same fallback as ChangedSince — we lost the old commit, can't compute deletions.
            // The caller's reconciliation pass (or next snapshot) will catch up.
            yield break;
        }

        var newCommit = repo.Head.Tip;
        if (oldCommit.Sha == newCommit.Sha)
        {
            // No new commits — check working-tree deletions only.
            var statusOptions = new StatusOptions { IncludeIgnored = false };
            foreach (var entry in repo.RetrieveStatus(statusOptions))
            {
                if ((entry.State & FileStatus.DeletedFromWorkdir) != 0
                    || (entry.State & FileStatus.DeletedFromIndex) != 0)
                {
                    yield return NormalizePath(entry.FilePath);
                }
            }
            yield break;
        }

        var diff = repo.Diff.Compare<TreeChanges>(oldCommit.Tree, newCommit.Tree);
        foreach (var change in diff)
        {
            if (change.Status == ChangeKind.Deleted)
                yield return NormalizePath(change.OldPath);
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}

/// <summary>
/// Per-watchpath snapshot of the git repository state at the end of a successful scan.
/// Persisted in the checkpoint store so the next cycle can compute incremental diffs.
/// </summary>
/// <param name="HeadSha">SHA of HEAD at last scan; empty string for a brand-new repo.</param>
/// <param name="DirtyPaths">Set of working-tree-dirty relative paths (forward-slash) at last scan.</param>
public sealed record GitWatchState(string HeadSha, IReadOnlySet<string> DirtyPaths)
{
    public static GitWatchState Empty { get; } = new(string.Empty, new HashSet<string>());
}
