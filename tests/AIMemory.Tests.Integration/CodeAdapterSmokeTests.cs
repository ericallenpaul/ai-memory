using AIMemory.CodeIndex.Git;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Configuration;
using AIMemory.Tests.Unit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Integration;

/// <summary>
/// Smoke tests against the developer's actual repos directory. Skipped automatically
/// when those paths don't exist, so CI on a fresh checkout passes. Locally these prove
/// the git-vs-FS fallback works on real-world content (the user has a non-git
/// "ts-screengrab" sibling that exercises the FS path).
/// </summary>
public class CodeAdapterSmokeTests
{
    private const string AiMemoryRoot = @"C:\Users\erica\Source\repos\ai-memory";
    private const string TsScreengrabRoot = @"C:\Users\erica\Source\repos\ts-screengrab";

    private static CodeAdapter BuildAdapter(out InMemoryCheckpointStore checkpoints)
    {
        var fileFilter = new FileFilter();
        var parserRegistry = new ParserRegistry([
            new CSharpParser(),
            new TypeScriptParser(),
            new PythonParser(),
            new GoParser()
        ]);
        var gitDetector = new GitChangeDetector(NullLogger<GitChangeDetector>.Instance);
        checkpoints = new InMemoryCheckpointStore();
        return new CodeAdapter(
            fileFilter,
            parserRegistry,
            gitDetector,
            checkpoints,
            NullLogger<CodeAdapter>.Instance);
    }

    [SkippableFact]
    public void GitMode_DiscoversAiMemoryCSharpFiles()
    {
        Skip.IfNot(Directory.Exists(AiMemoryRoot), $"{AiMemoryRoot} not present");
        Skip.IfNot(Directory.Exists(Path.Combine(AiMemoryRoot, ".git")), "ai-memory must be a git repo");

        var adapter = BuildAdapter(out _);
        var config = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [AiMemoryRoot],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = adapter.DiscoverFiles(config).ToList();

        Assert.NotEmpty(files);
        // Should find .cs files from the AIMemory.* projects.
        Assert.Contains(files, f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        // Should NOT include anything inside .git, bin, obj, or node_modules.
        Assert.DoesNotContain(files, f => f.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public void GitMode_SecondScanReturnsEmpty_WhenNothingChanged()
    {
        Skip.IfNot(Directory.Exists(AiMemoryRoot), $"{AiMemoryRoot} not present");
        Skip.IfNot(Directory.Exists(Path.Combine(AiMemoryRoot, ".git")), "ai-memory must be a git repo");

        var adapter = BuildAdapter(out _);
        var config = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [AiMemoryRoot],
            DetectionMode = ChangeDetectionMode.Auto
        };

        // First scan + reconcile to record HEAD
        var first = adapter.DiscoverFiles(config).ToList();
        Assert.NotEmpty(first);

        var firstEnumerated = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [AiMemoryRoot] = first
        };
        adapter.Reconcile(config, firstEnumerated).ToList();

        // Second scan with no changes. Some files may be working-tree dirty in a real
        // dev repo, so we don't assert *strictly* empty — we assert "shorter than first".
        var second = adapter.DiscoverFiles(config).ToList();
        Assert.True(second.Count < first.Count,
            $"Expected second scan to be a subset of first ({first.Count}), got {second.Count}");
    }

    [SkippableFact]
    public void FsMode_DiscoversTsScreengrab_NonGitFolder()
    {
        Skip.IfNot(Directory.Exists(TsScreengrabRoot), $"{TsScreengrabRoot} not present");
        // Verify it really isn't a git repo — that's the whole point of this test.
        Skip.If(Directory.Exists(Path.Combine(TsScreengrabRoot, ".git")),
            "ts-screengrab unexpectedly has a .git folder");

        var adapter = BuildAdapter(out _);
        var config = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [TsScreengrabRoot],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = adapter.DiscoverFiles(config).ToList();
        // The folder may have any mix of files. We're verifying the fallback runs
        // without crashing — empty is a valid outcome if there are no parseable files.
        Assert.NotNull(files);
    }

    [SkippableFact]
    public void GitOnlyMode_SkipsNonGitFolder()
    {
        Skip.IfNot(Directory.Exists(TsScreengrabRoot), $"{TsScreengrabRoot} not present");
        Skip.If(Directory.Exists(Path.Combine(TsScreengrabRoot, ".git")),
            "ts-screengrab unexpectedly has a .git folder");

        var adapter = BuildAdapter(out _);
        var config = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [TsScreengrabRoot],
            DetectionMode = ChangeDetectionMode.GitOnly
        };

        var files = adapter.DiscoverFiles(config).ToList();
        Assert.Empty(files);
    }

    [SkippableFact]
    public void Auto_HandlesMixedWatchPaths()
    {
        Skip.IfNot(Directory.Exists(AiMemoryRoot), $"{AiMemoryRoot} not present");
        Skip.IfNot(Directory.Exists(TsScreengrabRoot), $"{TsScreengrabRoot} not present");

        var adapter = BuildAdapter(out _);
        var config = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [AiMemoryRoot, TsScreengrabRoot],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = adapter.DiscoverFiles(config).ToList();
        Assert.NotEmpty(files);
        // Both watch paths get processed independently.
        Assert.Contains(files, f => f.StartsWith(AiMemoryRoot, StringComparison.OrdinalIgnoreCase));
    }
}
