using AIMemory.Api.Distributed;

namespace AIMemory.Tests.Unit.Distributed;

/// <summary>
/// Phase 7a: the distributed-config persistence is what survives service restarts and
/// drives the Kestrel listener choices on next start. Roundtrip must be lossless and the
/// load path must tolerate a missing/empty file.
/// </summary>
public class DistributedConfigStoreTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);

        var cfg = store.Load();

        Assert.False(cfg.Enabled);
        Assert.Equal("127.0.0.1", cfg.BindAddress);
        Assert.Equal(0, cfg.BindPort);
        Assert.Equal(string.Empty, cfg.TlsFingerprint);
    }

    [Fact]
    public void Load_ReturnsDefaults_OnMalformedFile()
    {
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);
        File.WriteAllText(store.FilePath, "{not valid json");

        var cfg = store.Load();

        // Don't crash — degrade gracefully so a corrupted file doesn't block startup.
        Assert.False(cfg.Enabled);
    }

    [Fact]
    public void Save_PersistsRoundtrip()
    {
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);

        store.Save(new DistributedConfig
        {
            Enabled = true,
            BindAddress = "0.0.0.0",
            BindPort = 5219,
            TlsFingerprint = "abcdef0123456789"
        });

        // New store instance, same disk state — simulates next service start reading config.
        var store2 = new DistributedConfigStore(temp.Path);
        var cfg = store2.Load();

        Assert.True(cfg.Enabled);
        Assert.Equal("0.0.0.0", cfg.BindAddress);
        Assert.Equal(5219, cfg.BindPort);
        Assert.Equal("abcdef0123456789", cfg.TlsFingerprint);
    }

    [Fact]
    public void Save_CreatesDirectoryWhenMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "aimemory-distributed-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Directory does not exist yet.
            Assert.False(Directory.Exists(path));
            var store = new DistributedConfigStore(path);

            store.Save(new DistributedConfig { Enabled = true });

            Assert.True(File.Exists(store.FilePath));
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aimemory-distributed-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
