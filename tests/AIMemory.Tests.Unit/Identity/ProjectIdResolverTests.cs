using AIMemory.Identity;
using LibGit2Sharp;

namespace AIMemory.Tests.Unit.Identity;

public class ProjectIdResolverTests : IDisposable
{
    private readonly string _root;

    public ProjectIdResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aimemory-pidr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // libgit2 holds memory-maps briefly; force GC and retry-delete.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); }
                        catch { /* best effort */ }
                    }
                    Directory.Delete(_root, true);
                }
                return;
            }
            catch when (i < 2) { Thread.Sleep(50); }
            catch { return; }
        }
    }

    // ---- URL normalization ---------------------------------------------------------------

    [Theory]
    [InlineData("https://github.com/Eric/AIMemory.git",     "https://github.com/eric/aimemory")]
    [InlineData("https://github.com/Eric/AIMemory/",        "https://github.com/eric/aimemory")]
    [InlineData("https://github.com/Eric/AIMemory",         "https://github.com/eric/aimemory")]
    [InlineData("git@github.com:Eric/AIMemory.git",         "https://github.com/eric/aimemory")]
    [InlineData("ssh://git@github.com/Eric/AIMemory.git",   "https://github.com/eric/aimemory")]
    [InlineData("ssh://git@github.com:22/Eric/AIMemory.git", "https://github.com/eric/aimemory")]
    [InlineData("https://token@gitlab.com/me/proj/",        "https://gitlab.com/me/proj")]
    [InlineData("https://user:pass@example.com/x.git",      "https://example.com/x")]
    [InlineData("ssh://git@bitbucket.org/team/repo",        "https://bitbucket.org/team/repo")]
    public void NormalizeRemoteUrl_FollowsRules(string input, string expected)
    {
        Assert.Equal(expected, ProjectIdResolver.NormalizeRemoteUrl(input));
    }

    [Fact]
    public void NormalizeRemoteUrl_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => ProjectIdResolver.NormalizeRemoteUrl(""));
    }

    // ---- Hash formulas -------------------------------------------------------------------

    [Fact]
    public void ComputeGitProjectId_IsDeterministic()
    {
        var a = ProjectIdResolver.ComputeGitProjectId("abc", "https://github.com/x/y");
        var b = ProjectIdResolver.ComputeGitProjectId("abc", "https://github.com/x/y");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void ComputeGitProjectId_DiffersOnCommitChange()
    {
        var a = ProjectIdResolver.ComputeGitProjectId("aaa", "https://github.com/x/y");
        var b = ProjectIdResolver.ComputeGitProjectId("bbb", "https://github.com/x/y");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeGitProjectId_DiffersOnUrlChange()
    {
        var a = ProjectIdResolver.ComputeGitProjectId("abc", "https://github.com/x/y");
        var b = ProjectIdResolver.ComputeGitProjectId("abc", "https://github.com/x/z");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeGitProjectId_NoCollisionFromConcatBoundaryAmbiguity()
    {
        // Without the \n separator, "aabb" + "ccdd" would collide with "aa" + "bbccdd".
        var a = ProjectIdResolver.ComputeGitProjectId("aabb", "ccdd");
        var b = ProjectIdResolver.ComputeGitProjectId("aa",   "bbccdd");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeFallbackProjectId_IncludesHostId()
    {
        var hostA = "host-a";
        var hostB = "host-b";
        var path = "/some/path";

        var a = ProjectIdResolver.ComputeFallbackProjectId(hostA, path);
        var b = ProjectIdResolver.ComputeFallbackProjectId(hostB, path);
        Assert.NotEqual(a, b);
    }

    // ---- Resolver behavior ---------------------------------------------------------------

    [Fact]
    public void Resolve_NonGitDirectory_ReturnsFallback()
    {
        var dir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(dir);
        var id = new ProjectIdResolver().Resolve(dir, "test-host");

        Assert.Equal("fallback", id.IdentityKind);
        Assert.Null(id.CanonicalRemoteUrl);
        Assert.Null(id.RootCommitSha);
        Assert.NotNull(id.Reason);
    }

    [Fact]
    public void Resolve_GitRepoNoRemote_ReturnsFallback()
    {
        var dir = Path.Combine(_root, "norepo");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);

        var sig = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        using (var repo = new Repository(dir))
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            Commands.Stage(repo, "a.txt");
            repo.Commit("initial", sig, sig);
        }

        var id = new ProjectIdResolver().Resolve(dir, "test-host");
        Assert.Equal("fallback", id.IdentityKind);
        Assert.Equal("no remote configured", id.Reason);
    }

    [Fact]
    public void Resolve_GitRepoWithRemote_ReturnsCanonicalGitId()
    {
        var dir = Path.Combine(_root, "withremote");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);

        var sig = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        Commit rootCommit;
        using (var repo = new Repository(dir))
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            Commands.Stage(repo, "a.txt");
            rootCommit = repo.Commit("initial", sig, sig);
            repo.Network.Remotes.Add("origin", "https://github.com/Acme/Tool.git");
        }

        var id = new ProjectIdResolver().Resolve(dir, "test-host");
        Assert.Equal("git", id.IdentityKind);
        Assert.Equal("https://github.com/acme/tool", id.CanonicalRemoteUrl);
        Assert.Equal(rootCommit.Sha.ToLowerInvariant(), id.RootCommitSha);

        var expectedId = ProjectIdResolver.ComputeGitProjectId(
            rootCommit.Sha.ToLowerInvariant(), "https://github.com/acme/tool");
        Assert.Equal(expectedId, id.ProjectId);
    }

    [Fact]
    public void Resolve_GitRepoSshRemote_NormalizesToHttps()
    {
        var dir = Path.Combine(_root, "ssh");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        var sig = new Signature("T", "t@e.com", DateTimeOffset.UtcNow);
        using (var repo = new Repository(dir))
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            Commands.Stage(repo, "a.txt");
            repo.Commit("initial", sig, sig);
            repo.Network.Remotes.Add("origin", "git@github.com:Acme/Tool.git");
        }

        var id = new ProjectIdResolver().Resolve(dir, "test-host");
        Assert.Equal("git", id.IdentityKind);
        Assert.Equal("https://github.com/acme/tool", id.CanonicalRemoteUrl);
    }

    [Fact]
    public void Resolve_GitRepoMultipleCommits_FindsRootCommit()
    {
        var dir = Path.Combine(_root, "multi");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        var sig = new Signature("T", "t@e.com", DateTimeOffset.UtcNow);
        Commit root;
        using (var repo = new Repository(dir))
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
            Commands.Stage(repo, "a.txt");
            root = repo.Commit("a", sig, sig);

            File.WriteAllText(Path.Combine(dir, "a.txt"), "2");
            Commands.Stage(repo, "a.txt");
            repo.Commit("b", sig, sig);

            File.WriteAllText(Path.Combine(dir, "a.txt"), "3");
            Commands.Stage(repo, "a.txt");
            repo.Commit("c", sig, sig);

            repo.Network.Remotes.Add("origin", "https://example.com/x.git");
        }

        var id = new ProjectIdResolver().Resolve(dir, "test-host");
        Assert.Equal("git", id.IdentityKind);
        Assert.Equal(root.Sha.ToLowerInvariant(), id.RootCommitSha);
    }

    [Fact]
    public void Resolve_NonExistentPath_ReturnsFallback()
    {
        var resolver = new ProjectIdResolver();
        var id = resolver.Resolve(Path.Combine(_root, "does-not-exist"), "test-host");
        Assert.Equal("fallback", id.IdentityKind);
    }

    [Fact]
    public void Resolve_PrefersOriginOverOtherRemotes()
    {
        var dir = Path.Combine(_root, "origin-priority");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        var sig = new Signature("T", "t@e.com", DateTimeOffset.UtcNow);
        using (var repo = new Repository(dir))
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
            Commands.Stage(repo, "a.txt");
            repo.Commit("initial", sig, sig);
            repo.Network.Remotes.Add("zfork",  "https://github.com/zfork/repo.git");
            repo.Network.Remotes.Add("origin", "https://github.com/origin/repo.git");
        }

        var id = new ProjectIdResolver().Resolve(dir, "test-host");
        Assert.Equal("https://github.com/origin/repo", id.CanonicalRemoteUrl);
    }

    [Fact]
    public void Resolve_RequiresNonEmptyHostId()
    {
        var resolver = new ProjectIdResolver();
        Assert.Throws<ArgumentException>(() => resolver.Resolve(_root, ""));
    }
}
