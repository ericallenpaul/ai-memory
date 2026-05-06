using AIMemory.CodeIndex.Git;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Unit.Git;

/// <summary>
/// Tests for the libgit2-backed Tier-0 change detector. Each test owns a fresh temp-dir
/// repository, exercises one scenario (clean/dirty/commits/deletes/reverts/non-git), and
/// disposes everything at the end. No fixture sharing — keeps a single failing test from
/// poisoning others.
/// </summary>
public sealed class GitChangeDetectorTests : IDisposable
{
    private readonly string _root;
    private readonly GitChangeDetector _detector = new(NullLogger<GitChangeDetector>.Instance);

    public GitChangeDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aimemory-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // libgit2 holds files open via memory mapping on Windows; clear read-only bits
            // and retry once if the first attempt fails.
            try { Directory.Delete(_root, recursive: true); }
            catch
            {
                ForceClearReadonly(_root);
                try { Directory.Delete(_root, recursive: true); } catch { /* leak the temp dir */ }
            }
        }
    }

    private static void ForceClearReadonly(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    private static Signature TestSig() => new("Test", "test@example.com", DateTimeOffset.UtcNow);

    private string InitRepo()
    {
        Repository.Init(_root);
        return _root;
    }

    private static string Commit(string repoPath, string relativePath, string content, string message)
    {
        var abs = Path.Combine(repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);

        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relativePath);
        var commit = repo.Commit(message, TestSig(), TestSig(), new CommitOptions());
        return commit.Sha;
    }

    private static void DeleteAndCommit(string repoPath, string relativePath, string message)
    {
        var abs = Path.Combine(repoPath, relativePath);
        File.Delete(abs);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relativePath);
        repo.Commit(message, TestSig(), TestSig(), new CommitOptions());
    }

    // ── IsGitRepository ────────────────────────────────────────────────

    [Fact]
    public void IsGitRepository_ReturnsTrue_ForInitializedRepo()
    {
        InitRepo();
        Assert.True(_detector.IsGitRepository(_root));
    }

    [Fact]
    public void IsGitRepository_ReturnsFalse_ForPlainFolder()
    {
        Assert.False(_detector.IsGitRepository(_root));
    }

    [Fact]
    public void IsGitRepository_ReturnsFalse_ForMissingPath()
    {
        var ghost = Path.Combine(_root, "does-not-exist");
        Assert.False(_detector.IsGitRepository(ghost));
    }

    [Fact]
    public void IsGitRepository_ReturnsFalse_ForEmptyString()
    {
        Assert.False(_detector.IsGitRepository(string.Empty));
    }

    // ── EnumerateAllFiles ──────────────────────────────────────────────

    [Fact]
    public void EnumerateAllFiles_ReturnsCommittedFiles()
    {
        InitRepo();
        Commit(_root, "src/Foo.cs", "class Foo {}", "init");
        Commit(_root, "src/Bar.cs", "class Bar {}", "second");

        var files = _detector.EnumerateAllFiles(_root).ToList();

        Assert.Contains(files, f => f.EndsWith("Foo.cs"));
        Assert.Contains(files, f => f.EndsWith("Bar.cs"));
    }

    [Fact]
    public void EnumerateAllFiles_IncludesUntrackedFiles()
    {
        InitRepo();
        Commit(_root, "tracked.cs", "class T {}", "init");

        // Untracked-but-not-ignored file
        File.WriteAllText(Path.Combine(_root, "untracked.cs"), "class U {}");

        var files = _detector.EnumerateAllFiles(_root).ToList();

        Assert.Contains(files, f => f.EndsWith("tracked.cs"));
        Assert.Contains(files, f => f.EndsWith("untracked.cs"));
    }

    [Fact]
    public void EnumerateAllFiles_ExcludesIgnoredFiles()
    {
        InitRepo();
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.tmp\n");
        Commit(_root, ".gitignore", "*.tmp\n", "ignore tmp");

        File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "junk");
        Commit(_root, "real.cs", "class R {}", "real");

        var files = _detector.EnumerateAllFiles(_root).ToList();

        Assert.DoesNotContain(files, f => f.EndsWith("scratch.tmp"));
        Assert.Contains(files, f => f.EndsWith("real.cs"));
    }

    // ── GetCurrentState ────────────────────────────────────────────────

    [Fact]
    public void GetCurrentState_ReturnsHeadSha_AfterCommit()
    {
        InitRepo();
        var sha = Commit(_root, "x.cs", "class X {}", "first");

        var state = _detector.GetCurrentState(_root);

        Assert.Equal(sha, state.HeadSha);
    }

    [Fact]
    public void GetCurrentState_IncludesWorkingTreeDirty()
    {
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");

        // Modify without committing
        File.WriteAllText(Path.Combine(_root, "x.cs"), "class X { void Y() {} }");

        var state = _detector.GetCurrentState(_root);

        Assert.Contains("x.cs", state.DirtyPaths);
    }

    [Fact]
    public void GetCurrentState_IncludesUntrackedInDirty()
    {
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");
        File.WriteAllText(Path.Combine(_root, "y.cs"), "class Y {}");

        var state = _detector.GetCurrentState(_root);

        Assert.Contains("y.cs", state.DirtyPaths);
    }

    // ── ChangedSince ───────────────────────────────────────────────────

    [Fact]
    public void ChangedSince_FirstScan_FallsBackToFullEnumerationViaCaller()
    {
        // ChangedSince with empty state returns nothing on its own — the first-scan
        // contract is: caller (CodeAdapter.EnumerateGitFiles) uses EnumerateAllFiles when
        // lastState.HeadSha is empty. ChangedSince still has to be safe to call though.
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");

        var changed = _detector.ChangedSince(_root, GitWatchState.Empty).ToList();

        // With no last state and no working-tree dirty, the result is empty (no commits
        // to diff against, no working-tree changes).
        Assert.Empty(changed);
    }

    [Fact]
    public void ChangedSince_NoNewCommitsOrChanges_ReturnsEmpty()
    {
        InitRepo();
        var sha = Commit(_root, "x.cs", "class X {}", "first");
        var state = new GitWatchState(sha, new HashSet<string>());

        var changed = _detector.ChangedSince(_root, state).ToList();

        Assert.Empty(changed);
    }

    [Fact]
    public void ChangedSince_NewCommit_ReturnsChangedFiles()
    {
        InitRepo();
        var firstSha = Commit(_root, "x.cs", "class X {}", "first");
        Commit(_root, "y.cs", "class Y {}", "second");

        var state = new GitWatchState(firstSha, new HashSet<string>());
        var changed = _detector.ChangedSince(_root, state).ToList();

        Assert.Contains(changed, f => f.EndsWith("y.cs"));
    }

    [Fact]
    public void ChangedSince_WorkingTreeDirty_IsIncluded()
    {
        InitRepo();
        var sha = Commit(_root, "x.cs", "class X {}", "first");
        File.WriteAllText(Path.Combine(_root, "x.cs"), "class X { void New() {} }");

        var state = new GitWatchState(sha, new HashSet<string>());
        var changed = _detector.ChangedSince(_root, state).ToList();

        Assert.Contains(changed, f => f.EndsWith("x.cs"));
    }

    [Fact]
    public void ChangedSince_RevertedDirtyFile_IsRescanned()
    {
        // A file that was dirty last scan but is now clean should still be re-enumerated
        // so the adapter's hash check can confirm content matches HEAD.
        InitRepo();
        var sha = Commit(_root, "x.cs", "class X {}", "first");

        var state = new GitWatchState(sha, new HashSet<string> { "x.cs" });
        var changed = _detector.ChangedSince(_root, state).ToList();

        Assert.Contains(changed, f => f.EndsWith("x.cs"));
    }

    [Fact]
    public void ChangedSince_UnknownHeadSha_FallsBackToFullEnumeration()
    {
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");
        Commit(_root, "y.cs", "class Y {}", "second");

        // SHA that doesn't exist in this repo (40 hex zeros)
        var phantom = new GitWatchState(new string('0', 40), new HashSet<string>());
        var changed = _detector.ChangedSince(_root, phantom).ToList();

        // Fall back: should yield all tracked files
        Assert.Contains(changed, f => f.EndsWith("x.cs"));
        Assert.Contains(changed, f => f.EndsWith("y.cs"));
    }

    [Fact]
    public void ChangedSince_DeletedFile_IsExcludedFromChangedSet()
    {
        // Deletes go through EnumerateDeletions, NOT ChangedSince. ChangedSince must not
        // emit a path that no longer exists.
        InitRepo();
        var firstSha = Commit(_root, "victim.cs", "class V {}", "first");
        DeleteAndCommit(_root, "victim.cs", "delete victim");

        var state = new GitWatchState(firstSha, new HashSet<string>());
        var changed = _detector.ChangedSince(_root, state).ToList();

        Assert.DoesNotContain(changed, f => f.EndsWith("victim.cs"));
    }

    // ── EnumerateDeletions ─────────────────────────────────────────────

    [Fact]
    public void EnumerateDeletions_CommittedDelete_ReturnsPath()
    {
        InitRepo();
        var firstSha = Commit(_root, "victim.cs", "class V {}", "first");
        DeleteAndCommit(_root, "victim.cs", "delete");

        var state = new GitWatchState(firstSha, new HashSet<string>());
        var deletions = _detector.EnumerateDeletions(_root, state).ToList();

        Assert.Contains("victim.cs", deletions);
    }

    [Fact]
    public void EnumerateDeletions_WorkingTreeDelete_ReturnsPath()
    {
        InitRepo();
        var sha = Commit(_root, "victim.cs", "class V {}", "first");

        // Delete from working tree without committing
        File.Delete(Path.Combine(_root, "victim.cs"));

        var state = new GitWatchState(sha, new HashSet<string>());
        var deletions = _detector.EnumerateDeletions(_root, state).ToList();

        Assert.Contains("victim.cs", deletions);
    }

    [Fact]
    public void EnumerateDeletions_NoDeletions_ReturnsEmpty()
    {
        InitRepo();
        var sha = Commit(_root, "x.cs", "class X {}", "first");
        Commit(_root, "y.cs", "class Y {}", "second");

        var state = new GitWatchState(sha, new HashSet<string>());
        var deletions = _detector.EnumerateDeletions(_root, state).ToList();

        Assert.Empty(deletions);
    }

    [Fact]
    public void EnumerateDeletions_MissingHeadSha_ReturnsEmpty()
    {
        // When we lose the recorded HEAD (rebase, etc.) we can't compute deletions.
        // Returning empty is correct — the caller's reconciliation pass catches up.
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");

        var phantom = new GitWatchState(new string('0', 40), new HashSet<string>());
        var deletions = _detector.EnumerateDeletions(_root, phantom).ToList();

        Assert.Empty(deletions);
    }

    [Fact]
    public void EnumerateDeletions_EmptyHeadSha_ReturnsEmpty()
    {
        InitRepo();
        Commit(_root, "x.cs", "class X {}", "first");

        var deletions = _detector.EnumerateDeletions(_root, GitWatchState.Empty).ToList();

        Assert.Empty(deletions);
    }
}
