namespace AIMemory.Ingestor.Checkpointing;

public interface ICheckpointStore
{
    Checkpoint? GetCheckpoint(string filePath);
    void SaveCheckpoint(string filePath, Checkpoint checkpoint);
    void Flush();
    Dictionary<string, Checkpoint> GetAll();
}

public class Checkpoint
{
    public long LastProcessedOffset { get; set; }
    public DateTimeOffset? LastProcessedTimestamp { get; set; }
    public long FileSize { get; set; }
    public DateTimeOffset? FileMtime { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? ContentHash { get; set; }
}
