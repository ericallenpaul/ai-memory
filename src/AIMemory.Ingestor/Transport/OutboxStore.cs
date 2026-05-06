using System.Text.Json;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Dtos;

namespace AIMemory.Ingestor.Transport;

public class OutboxStore
{
    private readonly string _outboxPath;
    private readonly int _maxSizeMb;
    private readonly ILogger<OutboxStore> _logger;

    public OutboxStore(string outboxPath, int maxSizeMb, ILogger<OutboxStore> logger)
    {
        _outboxPath = outboxPath;
        _maxSizeMb = maxSizeMb;
        _logger = logger;
        Directory.CreateDirectory(_outboxPath);
    }

    public void Enqueue(BatchIngestRequest request)
    {
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json";
        var filePath = Path.Combine(_outboxPath, fileName);
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(filePath, json);
        _logger.LogInformation("Enqueued batch to outbox: {File}", fileName);

        PurgeIfNeeded();
    }

    public IEnumerable<(string FilePath, BatchIngestRequest Request)> DequeueAll()
    {
        if (!Directory.Exists(_outboxPath)) yield break;

        foreach (var file in Directory.GetFiles(_outboxPath, "*.json").OrderBy(f => f))
        {
            BatchIngestRequest? request = null;
            try
            {
                var json = File.ReadAllText(file);
                request = JsonSerializer.Deserialize<BatchIngestRequest>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read outbox file {File}", file);
            }

            if (request != null)
                yield return (file, request);
        }
    }

    public void Remove(string filePath)
    {
        try
        {
            File.Delete(filePath);
            _logger.LogInformation("Removed outbox file: {File}", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove outbox file {File}", filePath);
        }
    }

    // Concurrent-safe: each Enqueue writes a unique GUID-named file, so parallel calls do not conflict.
    public int Count => Directory.Exists(_outboxPath) ? Directory.GetFiles(_outboxPath, "*.json").Length : 0;

    private void PurgeIfNeeded()
    {
        var dir = new DirectoryInfo(_outboxPath);
        if (!dir.Exists) return;

        var totalSize = dir.GetFiles("*.json").Sum(f => f.Length);
        if (totalSize <= _maxSizeMb * 1024L * 1024L) return;

        var files = dir.GetFiles("*.json").OrderBy(f => f.CreationTimeUtc).ToList();
        while (totalSize > _maxSizeMb * 1024L * 1024L && files.Count > 0)
        {
            var oldest = files[0];
            totalSize -= oldest.Length;
            oldest.Delete();
            files.RemoveAt(0);
            _logger.LogWarning("Purged old outbox file: {File}", oldest.Name);
        }
    }
}
