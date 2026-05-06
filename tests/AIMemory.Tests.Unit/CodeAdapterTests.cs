using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.CodeIndex.Git;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Models.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Unit;

public class CodeAdapterTests : IDisposable
{
    private readonly CodeAdapter _adapter;
    private readonly InMemoryCheckpointStore _checkpoints;
    private readonly string _tempDir;

    public CodeAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aimemory-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var fileFilter = new FileFilter();
        var parserRegistry = new ParserRegistry([new CSharpParser()]);
        var gitDetector = new GitChangeDetector(NullLogger<GitChangeDetector>.Instance);
        _checkpoints = new InMemoryCheckpointStore();
        var logger = NullLogger<CodeAdapter>.Instance;

        _adapter = new CodeAdapter(fileFilter, parserRegistry, gitDetector, _checkpoints, logger);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;
        // libgit2 leaves pack/object files read-only and may hold native memory maps for
        // a moment after Repository.Dispose. Force collection so finalizers release, clear
        // read-only bits, and retry up to 3x. If we still can't delete, leak the temp dir
        // rather than fail the test — Windows will clean it up eventually.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); }
                        catch { /* ignore — best effort */ }
                    }
                    Directory.Delete(_tempDir, recursive: true);
                }
                return;
            }
            catch when (i < 2)
            {
                Thread.Sleep(50);
            }
            catch { return; /* leak; don't fail the test */ }
        }
    }

    // Helpers

    private string WriteFile(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static Checkpoint MakeCheckpoint(FileInfo info, string content)
    {
        return new Checkpoint
        {
            FileMtime = info.LastWriteTimeUtc,
            FileSize = info.Length,
            ContentHash = ComputeHash(content),
            LastProcessedOffset = 1,
            LastSeenAt = DateTimeOffset.UtcNow
        };
    }

    // Tier 1: mtime + size unchanged — should skip

    [Fact]
    public void ReadNewRecords_UnchangedMtimeAndSize_YieldsNoRecords()
    {
        const string content = "public class Foo { }";
        var filePath = WriteFile("Foo.cs", content);
        var info = new FileInfo(filePath);

        var checkpoint = new Checkpoint
        {
            FileMtime = info.LastWriteTimeUtc,
            FileSize = info.Length,
            ContentHash = ComputeHash(content),
            LastProcessedOffset = 1,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        var records = _adapter.ReadNewRecords(filePath, checkpoint).ToList();

        Assert.Empty(records);
    }

    // Tier 2: mtime changed but content identical — should skip (touch-only change)

    [Fact]
    public void ReadNewRecords_ChangedMtimeButSameHash_YieldsNoRecords()
    {
        const string content = "public class Bar { }";
        var filePath = WriteFile("Bar.cs", content);
        var info = new FileInfo(filePath);

        // Simulate a "touch" — mtime in the future, but hash is current
        var checkpoint = new Checkpoint
        {
            FileMtime = info.LastWriteTimeUtc.AddSeconds(-5), // old mtime
            FileSize = info.Length,
            ContentHash = ComputeHash(content),              // same hash
            LastProcessedOffset = 1,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        var records = _adapter.ReadNewRecords(filePath, checkpoint).ToList();

        Assert.Empty(records);
    }

    // Tier 3: actual content change — should yield a record

    [Fact]
    public void ReadNewRecords_ContentChanged_YieldsOneRecord()
    {
        const string oldContent = "public class Baz { }";
        const string newContent = "public class Baz { public void Method() { } }";
        var filePath = WriteFile("Baz.cs", newContent);
        var info = new FileInfo(filePath);

        // Checkpoint reflects old content
        var checkpoint = new Checkpoint
        {
            FileMtime = info.LastWriteTimeUtc.AddSeconds(-10),
            FileSize = info.Length,
            ContentHash = ComputeHash(oldContent), // different hash
            LastProcessedOffset = 1,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        var records = _adapter.ReadNewRecords(filePath, checkpoint).ToList();

        Assert.Single(records);
        Assert.Equal(filePath, records[0].FilePath);
        Assert.Equal(ComputeHash(newContent), records[0].ContentHash);
    }

    // No checkpoint — first time seeing the file

    [Fact]
    public void ReadNewRecords_NullCheckpoint_YieldsOneRecord()
    {
        const string content = "public class New { }";
        var filePath = WriteFile("New.cs", content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();

        Assert.Single(records);
        Assert.Equal(filePath, records[0].FilePath);
    }

    // Non-existent file

    [Fact]
    public void ReadNewRecords_FileDoesNotExist_YieldsNoRecords()
    {
        var missingPath = Path.Combine(_tempDir, "missing.cs");

        var records = _adapter.ReadNewRecords(missingPath, checkpoint: null).ToList();

        Assert.Empty(records);
    }

    // ParseRecord tests

    [Fact]
    public void ParseRecord_AfterReadNewRecords_ProducesCodeFileUpsertEvent()
    {
        const string content = "public class Alpha { }";
        var filePath = WriteFile("Alpha.cs", content);

        // ReadNewRecords populates the file cache
        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        Assert.Single(records);

        var events = _adapter.ParseRecord(records[0]).ToList();

        Assert.Contains(events, e => e.Type == "CodeFileUpsert");
    }

    [Fact]
    public void ParseRecord_ForCSharpFile_ProducesCodeSymbolBatchEvent()
    {
        const string content = """
            namespace TestNs;

            public class Widget
            {
                public void Render() { }
            }
            """;

        var filePath = WriteFile("Widget.cs", content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        Assert.Single(records);

        var events = _adapter.ParseRecord(records[0]).ToList();

        Assert.Contains(events, e => e.Type == "CodeSymbolBatch");
    }

    [Fact]
    public void ParseRecord_CodeFileUpsertEvent_HasCorrectLanguage()
    {
        const string content = "public class Gamma { }";
        var filePath = WriteFile("Gamma.cs", content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        var events = _adapter.ParseRecord(records[0]).ToList();

        var upsertEvent = events.First(e => e.Type == "CodeFileUpsert");
        var payload = upsertEvent.Payload.Deserialize<CodeFileUpsertEvent>();
        Assert.NotNull(payload);
        Assert.Equal("csharp", payload.Language);
    }

    [Fact]
    public void ParseRecord_CodeFileUpsertEvent_HasCorrectContentHash()
    {
        const string content = "public class HashCheck { }";
        var filePath = WriteFile("HashCheck.cs", content);
        var expectedHash = ComputeHash(content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        var events = _adapter.ParseRecord(records[0]).ToList();

        var upsertEvent = events.First(e => e.Type == "CodeFileUpsert");
        var payload = upsertEvent.Payload.Deserialize<CodeFileUpsertEvent>();
        Assert.NotNull(payload);
        Assert.Equal(expectedHash, payload.ContentHash);
    }

    [Fact]
    public void ParseRecord_WithoutPriorRead_YieldsNoEvents()
    {
        // ParseRecord with a RawRecord that was never populated via ReadNewRecords
        var rawRecord = new AIMemory.Ingestor.Adapters.RawRecord
        {
            FilePath = Path.Combine(_tempDir, "ghost.cs"),
            Offset = 1,
            Line = "",
            Source = "code-index"
        };

        var events = _adapter.ParseRecord(rawRecord).ToList();

        Assert.Empty(events);
    }

    [Fact]
    public void ParseRecord_IdempotencyKey_ContainsFilePath()
    {
        const string content = "public class IdCheck { }";
        var filePath = WriteFile("IdCheck.cs", content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        var events = _adapter.ParseRecord(records[0]).ToList();

        var upsertEvent = events.First(e => e.Type == "CodeFileUpsert");
        Assert.Contains(filePath, upsertEvent.IdempotencyKey);
    }

    [Fact]
    public void ParseRecord_CodeSymbolBatch_ContainsSymbolsFromParser()
    {
        const string content = """
            namespace Ns;

            public class Engine
            {
                public void Start() { }
                public void Stop() { }
            }
            """;

        var filePath = WriteFile("Engine.cs", content);

        var records = _adapter.ReadNewRecords(filePath, checkpoint: null).ToList();
        var events = _adapter.ParseRecord(records[0]).ToList();

        var symbolBatch = events.First(e => e.Type == "CodeSymbolBatch");
        var payload = symbolBatch.Payload.Deserialize<CodeSymbolBatchEvent>();
        Assert.NotNull(payload);
        Assert.True(payload.Symbols.Count >= 2, "Expected at least Engine class and one method");
    }

    // SourceName

    [Fact]
    public void SourceName_ReturnsCodeIndex()
    {
        Assert.Equal("code-index", _adapter.SourceName);
    }

    // ── Git tier (Phase 1) ─────────────────────────────────────────────

    private static LibGit2Sharp.Signature TestSig() =>
        new("Test", "test@example.com", DateTimeOffset.UtcNow);

    private static string CommitFile(string repoPath, string relativePath, string content, string message)
    {
        var abs = Path.Combine(repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);

        using var repo = new LibGit2Sharp.Repository(repoPath);
        LibGit2Sharp.Commands.Stage(repo, relativePath);
        return repo.Commit(message, TestSig(), TestSig(), new LibGit2Sharp.CommitOptions()).Sha;
    }

    [Fact]
    public void DiscoverFiles_GitMode_FirstScan_ReturnsAllTrackedFiles()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        CommitFile(_tempDir, "Foo.cs", "class Foo {}", "init");
        CommitFile(_tempDir, "Bar.cs", "class Bar {}", "second");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Contains(files, f => f.EndsWith("Foo.cs"));
        Assert.Contains(files, f => f.EndsWith("Bar.cs"));
    }

    [Fact]
    public void DiscoverFiles_GitMode_SubsequentScanWithNoChanges_ReturnsEmpty()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        CommitFile(_tempDir, "Foo.cs", "class Foo {}", "init");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        // First scan: enumerate everything + run reconcile to persist watchpath checkpoint.
        var firstFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        Assert.NotEmpty(firstFiles);

        var enumeratedFirstScan = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [_tempDir] = firstFiles
        };
        _adapter.Reconcile(sourceConfig, enumeratedFirstScan).ToList();

        // Second scan with no changes
        var secondFiles = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Empty(secondFiles);
    }

    [Fact]
    public void DiscoverFiles_GitMode_OnlyChangedFile_AfterCommit()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        CommitFile(_tempDir, "Foo.cs", "class Foo {}", "init");
        CommitFile(_tempDir, "Bar.cs", "class Bar {}", "init Bar");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        // First scan + reconcile to capture HEAD
        var firstFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        var enumerated = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [_tempDir] = firstFiles
        };
        _adapter.Reconcile(sourceConfig, enumerated).ToList();

        // Modify Bar.cs and commit — only Bar should be re-enumerated
        CommitFile(_tempDir, "Bar.cs", "class Bar { void Method() {} }", "update Bar");

        var secondScan = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Single(secondScan);
        Assert.EndsWith("Bar.cs", secondScan[0]);
    }

    [Fact]
    public void DiscoverFiles_FsOnly_BypassesGit()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        CommitFile(_tempDir, "Foo.cs", "class Foo {}", "init");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.FsOnly
        };

        // First call captures via FS walk
        var firstFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        Assert.Contains(firstFiles, f => f.EndsWith("Foo.cs"));

        // Run reconcile to update FS-mode checkpoint
        var enumerated = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [_tempDir] = firstFiles
        };
        _adapter.Reconcile(sourceConfig, enumerated).ToList();

        // Second call should still walk the filesystem (FS mode never narrows the set)
        var secondFiles = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Contains(secondFiles, f => f.EndsWith("Foo.cs"));
    }

    [Fact]
    public void DiscoverFiles_NonGitFolder_Auto_FallsBackToFsWalk()
    {
        // No git init — Auto mode should fall through to FS walk.
        WriteFile("Solo.cs", "class Solo {}");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Contains(files, f => f.EndsWith("Solo.cs"));
    }

    [Fact]
    public void DiscoverFiles_NonGitFolder_GitOnly_ReturnsEmpty()
    {
        WriteFile("Lonely.cs", "class Lonely {}");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.GitOnly
        };

        var files = _adapter.DiscoverFiles(sourceConfig).ToList();

        Assert.Empty(files);
    }

    // ── Reconcile / deletion detection ─────────────────────────────────

    [Fact]
    public void Reconcile_GitMode_CommittedDelete_EmitsCodeFileDeleteEvent()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        CommitFile(_tempDir, "Victim.cs", "class V {}", "first");
        var firstSha = CommitFile(_tempDir, "Other.cs", "class O {}", "second");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        // First scan + reconcile records the current HEAD
        var firstFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = firstFiles })
            .ToList();

        // Delete and commit
        File.Delete(Path.Combine(_tempDir, "Victim.cs"));
        using (var repo = new LibGit2Sharp.Repository(_tempDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "Victim.cs");
            repo.Commit("delete Victim", TestSig(), TestSig(), new LibGit2Sharp.CommitOptions());
        }

        // Second scan + reconcile should emit the delete event
        var secondFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        var events = _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = secondFiles })
            .ToList();

        var deleteEvent = events.SingleOrDefault(e => e.Type == "CodeFileDelete");
        Assert.NotNull(deleteEvent);
        var payload = deleteEvent.Payload.Deserialize<CodeFileDeleteEvent>();
        Assert.NotNull(payload);
        Assert.Equal("Victim.cs", payload.FilePath);
    }

    [Fact]
    public void Reconcile_FsMode_DeletedFile_EmitsCodeFileDeleteEvent()
    {
        WriteFile("Stable.cs", "class S {}");
        WriteFile("Doomed.cs", "class D {}");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.FsOnly
        };

        // First scan + reconcile to record the enumerated set
        var firstFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = firstFiles })
            .ToList();

        // Delete one file
        File.Delete(Path.Combine(_tempDir, "Doomed.cs"));

        // Second scan + reconcile should emit the delete
        var secondFiles = _adapter.DiscoverFiles(sourceConfig).ToList();
        var events = _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = secondFiles })
            .ToList();

        var deleteEvent = events.SingleOrDefault(e => e.Type == "CodeFileDelete");
        Assert.NotNull(deleteEvent);
        var payload = deleteEvent.Payload.Deserialize<CodeFileDeleteEvent>();
        Assert.NotNull(payload);
        Assert.Equal("Doomed.cs", payload.FilePath);
    }

    [Fact]
    public void Reconcile_NoChanges_EmitsNoEvents()
    {
        WriteFile("Constant.cs", "class C {}");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.FsOnly
        };

        var first = _adapter.DiscoverFiles(sourceConfig).ToList();
        _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = first })
            .ToList();

        var second = _adapter.DiscoverFiles(sourceConfig).ToList();
        var events = _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = second })
            .ToList();

        Assert.DoesNotContain(events, e => e.Type == "CodeFileDelete");
    }

    [Fact]
    public void Reconcile_PersistsCheckpoint_ForNextCycle()
    {
        LibGit2Sharp.Repository.Init(_tempDir);
        var sha = CommitFile(_tempDir, "X.cs", "class X {}", "first");

        var sourceConfig = new SourceConfig
        {
            Name = "code-index",
            WatchPaths = [_tempDir],
            DetectionMode = ChangeDetectionMode.Auto
        };

        var files = _adapter.DiscoverFiles(sourceConfig).ToList();
        _adapter.Reconcile(sourceConfig,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [_tempDir] = files })
            .ToList();

        var saved = _checkpoints.GetCheckpoint(CheckpointKey.ForWatchPath(_tempDir));
        Assert.NotNull(saved);
        Assert.Equal(sha, saved.GitHeadSha);
        Assert.NotNull(saved.LastEnumeratedFiles);
        Assert.Contains(saved.LastEnumeratedFiles, p => p.EndsWith("X.cs"));
    }
}
