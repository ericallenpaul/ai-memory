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

    // Per-watchpath git state. Populated only on watchpath-keyed checkpoints
    // (those whose key starts with WatchPathKeyPrefix); ignored on per-file checkpoints.
    public string? GitHeadSha { get; set; }
    public string[]? GitDirtyPaths { get; set; }

    // Per-watchpath FS-mode reconciliation: the set of relative paths the adapter
    // enumerated on its last successful scan. Used by the deletion sweep to emit
    // CodeFileDeleteEvent for files that disappeared between cycles.
    public string[]? LastEnumeratedFiles { get; set; }
}

public static class CheckpointKey
{
    /// <summary>
    /// Checkpoints whose key begins with this prefix are treated as per-watchpath
    /// state rather than per-file state. The remainder of the key is the absolute
    /// watch path, so two adapters watching different roots can coexist.
    /// </summary>
    public const string WatchPathKeyPrefix = "watchpath::";

    public static string ForWatchPath(string watchPath) => $"{WatchPathKeyPrefix}{watchPath}";
}
