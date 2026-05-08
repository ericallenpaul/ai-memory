namespace AIMemory.Ingestor.Configuration;

public class IngestorConfig
{
    public string AIMemoryApiBaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string ClientId { get; set; } = Environment.MachineName;
    public List<SourceConfig> Sources { get; set; } = [];
    public int ScanIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
    public RedactionConfig Redaction { get; set; } = new();
    public OutboxConfig Outbox { get; set; } = new();

    /// <summary>
    /// Operating mode for this ingestor. <see cref="IngestorMode.Local"/> targets a primary
    /// running on the same machine (today: HTTP loopback through the existing client).
    /// <see cref="IngestorMode.Remote"/> talks to a remote primary over HTTPS with a pinned
    /// certificate fingerprint.
    /// </summary>
    public IngestorMode Mode { get; set; } = IngestorMode.Local;

    /// <summary>
    /// Settings used when <see cref="Mode"/> is <see cref="IngestorMode.Remote"/>. Required
    /// fields are validated at startup — see <c>RemoteSinkConfigValidator</c>.
    /// </summary>
    public RemoteSinkConfig Remote { get; set; } = new();
}

/// <summary>
/// Selects the <c>ILedgerSink</c> implementation. Bound from configuration as a string;
/// values are case-insensitive (<c>"local"</c> or <c>"remote"</c>).
/// </summary>
public enum IngestorMode
{
    Local = 0,
    Remote = 1
}

/// <summary>
/// Configuration for the remote-mode <c>RemoteSink</c>. All three fields are required when
/// <see cref="IngestorConfig.Mode"/> is <see cref="IngestorMode.Remote"/>.
/// </summary>
public class RemoteSinkConfig
{
    /// <summary>Base URL of the paired primary, e.g. <c>https://eric-desktop.lan:5219</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Ingest-scoped API key issued by the primary's pairing flow.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Pinned SHA-256 fingerprint of the primary's leaf TLS certificate. Lowercase hex of the
    /// DER bytes (no colons), or with colons — the validator accepts either.
    /// </summary>
    public string PinnedCertFingerprint { get; set; } = string.Empty;
}

public class SourceConfig
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string AdapterType { get; set; } = string.Empty;
    public List<string> WatchPaths { get; set; } = [];
    public List<string> FilePatterns { get; set; } = [];
    public string Segment { get; set; } = "personal";

    /// <summary>
    /// Code-adapter-only: how to detect file changes within each WatchPath.
    /// <list type="bullet">
    ///   <item><c>Auto</c> (default) — use git when the path is a git repo, fall back to filesystem walk otherwise.</item>
    ///   <item><c>GitOnly</c> — require a git repo; non-git watch paths are skipped with a warning.</item>
    ///   <item><c>FsOnly</c> — always use the filesystem walk, even if the path is a git repo.</item>
    /// </list>
    /// Filesystem mtime/size/hash detection still runs inside whichever set git (or the walk) hands back —
    /// git is a tier-0 filter, not a replacement for content-hash verification.
    /// </summary>
    public ChangeDetectionMode DetectionMode { get; set; } = ChangeDetectionMode.Auto;
}

public enum ChangeDetectionMode
{
    Auto = 0,
    GitOnly = 1,
    FsOnly = 2
}

public class RedactionConfig
{
    public string Mode { get; set; } = "basic";
    public string? RulesFile { get; set; }
    public List<string> Exclusions { get; set; } = [];
}

public class OutboxConfig
{
    public string Path { get; set; } = "";
    public int MaxSizeMb { get; set; } = 100;
}
