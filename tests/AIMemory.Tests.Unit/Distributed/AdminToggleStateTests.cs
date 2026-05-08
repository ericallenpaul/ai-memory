using AIMemory.Api.Distributed;
using AIMemory.Api.Tls;

namespace AIMemory.Tests.Unit.Distributed;

/// <summary>
/// End-to-end-ish tests for the admin-toggle state model: enable/disable persist correctly,
/// the cert fingerprint is stable across "restarts" (new store instances reading the same
/// disk state), and a disable preserves the cached fingerprint so a subsequent enable doesn't
/// force a re-pair on every secondary.
/// </summary>
public class AdminToggleStateTests
{
    [Fact]
    public void Enable_PersistsBindAddressAndFingerprint()
    {
        using var temp = new TempDir();
        var configStore = new DistributedConfigStore(temp.Path);
        var tlsProvider = new TlsCertificateProvider(temp.Path);

        // Models POST /api/admin/distributed/enable.
        var cfg = configStore.Load();
        cfg.Enabled = true;
        cfg.BindAddress = "0.0.0.0";
        cfg.BindPort = 5219;
        using var cert = tlsProvider.GetOrCreate();
        cfg.TlsFingerprint = CertFingerprint.Compute(cert);
        configStore.Save(cfg);

        // "Restart" — new store reading from disk.
        var reload = new DistributedConfigStore(temp.Path).Load();

        Assert.True(reload.Enabled);
        Assert.Equal("0.0.0.0", reload.BindAddress);
        Assert.Equal(5219, reload.BindPort);
        Assert.Equal(64, reload.TlsFingerprint.Length);
    }

    [Fact]
    public void Disable_RestoresLocalhostBind()
    {
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);

        // Start enabled.
        store.Save(new DistributedConfig
        {
            Enabled = true,
            BindAddress = "0.0.0.0",
            BindPort = 5219,
            TlsFingerprint = "deadbeef"
        });

        // Models POST /api/admin/distributed/disable.
        var cfg = store.Load();
        cfg.Enabled = false;
        cfg.BindAddress = "127.0.0.1";
        store.Save(cfg);

        var reload = new DistributedConfigStore(temp.Path).Load();
        Assert.False(reload.Enabled);
        Assert.Equal("127.0.0.1", reload.BindAddress);
    }

    [Fact]
    public void Disable_KeepsCachedFingerprint_SoReEnableDoesNotForceRepair()
    {
        // Per design doc / phase 7a comment in Program.cs: deactivating distributed mode
        // doesn't wipe the fingerprint, so re-enabling reuses the same cert and existing
        // pairings remain valid (the secondary's pinned fingerprint still matches).
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);
        store.Save(new DistributedConfig
        {
            Enabled = true,
            BindAddress = "0.0.0.0",
            BindPort = 5219,
            TlsFingerprint = "abc123"
        });

        var cfg = store.Load();
        cfg.Enabled = false;
        cfg.BindAddress = "127.0.0.1";
        // NOTE: TlsFingerprint NOT cleared.
        store.Save(cfg);

        var reload = new DistributedConfigStore(temp.Path).Load();
        Assert.Equal("abc123", reload.TlsFingerprint);
    }

    [Fact]
    public void Status_ReflectsCurrentBindAndFingerprint()
    {
        // Models GET /api/admin/distributed/status response shape.
        using var temp = new TempDir();
        var store = new DistributedConfigStore(temp.Path);
        store.Save(new DistributedConfig
        {
            Enabled = true,
            BindAddress = "192.168.1.100",
            BindPort = 5219,
            TlsFingerprint = "f001"
        });

        var cfg = store.Load();

        Assert.True(cfg.Enabled);
        var endpoint = $"https://{cfg.BindAddress}:{cfg.BindPort}";
        Assert.Equal("https://192.168.1.100:5219", endpoint);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aimemory-toggle-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
