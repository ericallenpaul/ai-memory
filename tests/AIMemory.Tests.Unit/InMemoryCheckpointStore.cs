using AIMemory.Ingestor.Checkpointing;

namespace AIMemory.Tests.Unit;

/// <summary>
/// In-memory ICheckpointStore for tests. Avoids hitting %APPDATA% during xUnit runs.
/// </summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly Dictionary<string, Checkpoint> _store = new();

    public Checkpoint? GetCheckpoint(string filePath)
        => _store.TryGetValue(filePath, out var c) ? c : null;

    public void SaveCheckpoint(string filePath, Checkpoint checkpoint)
    {
        checkpoint.LastSeenAt = DateTimeOffset.UtcNow;
        _store[filePath] = checkpoint;
    }

    public void Flush() { }

    public Dictionary<string, Checkpoint> GetAll()
        => new Dictionary<string, Checkpoint>(_store);
}
