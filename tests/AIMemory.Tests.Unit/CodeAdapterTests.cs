using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly string _tempDir;

    public CodeAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aimemory-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var fileFilter = new FileFilter();
        var parserRegistry = new ParserRegistry([new CSharpParser()]);
        var logger = NullLogger<CodeAdapter>.Instance;

        _adapter = new CodeAdapter(fileFilter, parserRegistry, logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
}
