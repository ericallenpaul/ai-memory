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
