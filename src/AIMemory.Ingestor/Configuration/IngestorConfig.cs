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
