namespace AIMemory.Models.Entities;

public class IngestionLogEntry
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public long RecordOffset { get; set; }
    public string Status { get; set; } = "ok";
    public string? MachineName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
