using System.Text.Json;

namespace AIMemory.Api.Distributed;

/// <summary>
/// Persistent state for the "Allow remote ingestors" toggle. Lives at
/// <c>%ProgramData%\AIMemory\Api\distributed.json</c> so the bind interface and TLS state
/// survive service restarts and are visible to the desktop status endpoint.
///
/// <para>Phase 7a: read at startup to choose Kestrel listener bindings; written by the
/// <c>POST /api/admin/distributed/enable|disable</c> endpoints. The actual rebind happens
/// on next service start — see <c>Program.cs</c> for the wiring and the user-facing note.</para>
/// </summary>
public sealed class DistributedConfig
{
    /// <summary>True when the user has enabled remote-ingestor mode.</summary>
    public bool Enabled { get; set; }

    /// <summary>Kestrel bind address. Default <c>127.0.0.1</c>; <c>0.0.0.0</c> when distributed.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Kestrel port. 0 = pick at runtime.</summary>
    public int BindPort { get; set; }

    /// <summary>Cached lowercase-hex SHA-256 fingerprint of the active TLS cert. Empty when
    /// distributed mode is off (no cert generated yet).</summary>
    public string TlsFingerprint { get; set; } = string.Empty;
}

/// <summary>
/// File-backed store for <see cref="DistributedConfig"/>. Read/write is JSON; the file is
/// created on first write.
/// </summary>
public sealed class DistributedConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public DistributedConfigStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _path = System.IO.Path.Combine(baseDirectory, "distributed.json");
    }

    /// <summary>Filesystem path to the JSON store. Useful for diagnostics + tests.</summary>
    public string FilePath => _path;

    public DistributedConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new DistributedConfig();
            try
            {
                var raw = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<DistributedConfig>(raw, JsonOpts) ?? new DistributedConfig();
            }
            catch
            {
                // Malformed file = treat as fresh; avoid blocking startup on a bad write.
                return new DistributedConfig();
            }
        }
    }

    public void Save(DistributedConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var raw = JsonSerializer.Serialize(cfg, JsonOpts);
            File.WriteAllText(_path, raw);
        }
    }
}
