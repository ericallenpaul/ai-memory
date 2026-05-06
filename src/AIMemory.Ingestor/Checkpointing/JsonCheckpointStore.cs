using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AIMemory.Ingestor.Checkpointing;

public class JsonCheckpointStore : ICheckpointStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonCheckpointStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, Checkpoint> _checkpoints;

    public JsonCheckpointStore(string filePath, ILogger<JsonCheckpointStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
        _checkpoints = Load();
    }

    public Checkpoint? GetCheckpoint(string filePath)
    {
        lock (_lock)
        {
            return _checkpoints.GetValueOrDefault(filePath);
        }
    }

    public void SaveCheckpoint(string filePath, Checkpoint checkpoint)
    {
        lock (_lock)
        {
            checkpoint.LastSeenAt = DateTimeOffset.UtcNow;
            _checkpoints[filePath] = checkpoint;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(new CheckpointFile { Checkpoints = _checkpoints },
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush checkpoints to {Path}", _filePath);
            }
        }
    }

    public Dictionary<string, Checkpoint> GetAll()
    {
        lock (_lock)
        {
            return new Dictionary<string, Checkpoint>(_checkpoints);
        }
    }

    private Dictionary<string, Checkpoint> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, Checkpoint>();

            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<CheckpointFile>(json);
            return file?.Checkpoints ?? new Dictionary<string, Checkpoint>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checkpoints from {Path}, starting fresh", _filePath);
            return new Dictionary<string, Checkpoint>();
        }
    }

    private class CheckpointFile
    {
        public Dictionary<string, Checkpoint> Checkpoints { get; set; } = new();
    }
}
