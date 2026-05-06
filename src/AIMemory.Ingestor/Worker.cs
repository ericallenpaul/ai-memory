using System.Text.Json;
using Microsoft.Extensions.Options;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Redaction;
using AIMemory.Ingestor.Transport;
using AIMemory.Models.Dtos;
using AIMemory.Models.Events;

namespace AIMemory.Ingestor;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IngestorConfig _config;
    private readonly IEnumerable<ISourceAdapter> _adapters;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IRedactionPipeline _redaction;
    private readonly IAIMemoryClient _client;
    private readonly OutboxStore _outbox;
    private readonly bool _runOnce;

    private long _eventsIngested;
    private long _eventsFailed;

    public Worker(
        ILogger<Worker> logger,
        IOptions<IngestorConfig> config,
        IEnumerable<ISourceAdapter> adapters,
        ICheckpointStore checkpointStore,
        IRedactionPipeline redaction,
        IAIMemoryClient client,
        OutboxStore outbox,
        IConfiguration appConfig)
    {
        _logger = logger;
        _config = config.Value;
        _adapters = adapters;
        _checkpointStore = checkpointStore;
        _redaction = redaction;
        _client = client;
        _outbox = outbox;
        _runOnce = appConfig.GetValue<bool>("RunOnce");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AIMemory Ingestor starting (ClientId={ClientId}, RunOnce={RunOnce})",
            _config.ClientId, _runOnce);

        do
        {
            try
            {
                await RetryOutboxAsync(stoppingToken);
                await ScanAndIngestAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during ingestion cycle");
            }

            if (!_runOnce)
                await Task.Delay(TimeSpan.FromSeconds(_config.ScanIntervalSeconds), stoppingToken);

        } while (!_runOnce && !stoppingToken.IsCancellationRequested);

        _logger.LogInformation("Ingestor completed. Events ingested: {Ingested}, failed: {Failed}",
            _eventsIngested, _eventsFailed);
    }

    private async Task ScanAndIngestAsync(CancellationToken ct)
    {
        var tasks = _config.Sources.Where(s => s.Enabled)
            .Select(sc => ProcessSourceAsync(sc, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessSourceAsync(SourceConfig sourceConfig, CancellationToken ct)
    {
        var adapter = _adapters.FirstOrDefault(a => a.SourceName == sourceConfig.Name);
        if (adapter == null)
        {
            _logger.LogWarning("No adapter found for source '{Source}'", sourceConfig.Name);
            return;
        }

        // Bucket the enumerated files by watch path so the adapter's Reconcile pass can
        // compute deletions per-watchpath without re-walking. Adapters that don't care
        // about per-watchpath state (transcript adapters) still benefit from a free list.
        var enumeratedByWatchPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var wp in sourceConfig.WatchPaths)
            enumeratedByWatchPath[wp] = new List<string>();

        var files = new List<string>();
        foreach (var f in adapter.DiscoverFiles(sourceConfig))
        {
            files.Add(f);
            var matchedWatchPath = sourceConfig.WatchPaths
                .OrderByDescending(wp => wp.Length)
                .FirstOrDefault(wp => f.StartsWith(wp, StringComparison.OrdinalIgnoreCase));
            if (matchedWatchPath != null)
                enumeratedByWatchPath[matchedWatchPath].Add(f);
        }

        _logger.LogInformation("Source '{Source}': discovered {Count} files", sourceConfig.Name, files.Count);

        foreach (var filePath in files)
        {
            if (_redaction.ShouldExcludeFile(filePath))
            {
                _logger.LogDebug("Excluded file: {File}", filePath);
                continue;
            }

            var checkpoint = _checkpointStore.GetCheckpoint(filePath);
            var records = adapter.ReadNewRecords(filePath, checkpoint).ToList();

            if (records.Count == 0) continue;

            foreach (var record in records)
                record.Segment = sourceConfig.Segment;

            _logger.LogInformation("Processing {Count} new records from {File}", records.Count, filePath);

            var allEvents = new List<IngestEvent>();
            long maxOffset = checkpoint?.LastProcessedOffset ?? 0;

            foreach (var record in records)
            {
                var events = adapter.ParseRecord(record).ToList();
                foreach (var evt in events)
                {
                    // Apply redaction to message content
                    if (evt.Type == "MessageAppend" && _redaction.IsEnabled)
                    {
                        var payload = JsonSerializer.Deserialize<MessageAppendEvent>(evt.Payload.GetRawText());
                        if (payload != null)
                        {
                            payload.Content = _redaction.Redact(payload.Content);
                            evt.Payload = JsonSerializer.SerializeToElement(payload);
                        }
                    }

                    allEvents.Add(evt);
                }

                if (record.Offset >= maxOffset)
                    maxOffset = record.Offset + 1;
            }

            // Send in batches
            foreach (var batch in allEvents.Chunk(_config.BatchSize))
            {
                var request = new BatchIngestRequest
                {
                    ClientId = _config.ClientId,
                    MachineName = Environment.MachineName,
                    Source = adapter.SourceName,
                    Events = batch.ToList()
                };

                var result = await _client.SendBatchAsync(request, ct);
                if (result != null)
                {
                    Interlocked.Add(ref _eventsIngested, result.Succeeded);
                    Interlocked.Add(ref _eventsFailed, result.Failed);
                }
                else
                {
                    _outbox.Enqueue(request);
                    Interlocked.Add(ref _eventsFailed, batch.Length);
                }
            }

            // Update checkpoint
            var fileInfo = new FileInfo(filePath);
            var lastContentHash = records.LastOrDefault(r => r.ContentHash != null)?.ContentHash;
            _checkpointStore.SaveCheckpoint(filePath, new Checkpoint
            {
                LastProcessedOffset = maxOffset,
                LastProcessedTimestamp = DateTimeOffset.UtcNow,
                FileSize = fileInfo.Exists ? fileInfo.Length : 0,
                FileMtime = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : null,
                ContentHash = lastContentHash
            });
            _checkpointStore.Flush();
        }

        // End-of-cycle: ask the adapter for any reconciliation events (deletions, etc.)
        // and ship them on the same batch pipeline.
        var enumeratedView = enumeratedByWatchPath.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        var reconcileEvents = adapter.Reconcile(sourceConfig, enumeratedView).ToList();
        if (reconcileEvents.Count > 0)
        {
            _logger.LogInformation("Source '{Source}': reconcile produced {Count} events",
                sourceConfig.Name, reconcileEvents.Count);

            foreach (var batch in reconcileEvents.Chunk(_config.BatchSize))
            {
                var request = new BatchIngestRequest
                {
                    ClientId = _config.ClientId,
                    MachineName = Environment.MachineName,
                    Source = adapter.SourceName,
                    Events = batch.ToList()
                };

                var result = await _client.SendBatchAsync(request, ct);
                if (result != null)
                {
                    Interlocked.Add(ref _eventsIngested, result.Succeeded);
                    Interlocked.Add(ref _eventsFailed, result.Failed);
                }
                else
                {
                    _outbox.Enqueue(request);
                    Interlocked.Add(ref _eventsFailed, batch.Length);
                }
            }
            _checkpointStore.Flush();
        }
    }

    private async Task RetryOutboxAsync(CancellationToken ct)
    {
        if (_outbox.Count == 0) return;

        _logger.LogInformation("Retrying {Count} outbox items", _outbox.Count);

        foreach (var (filePath, request) in _outbox.DequeueAll())
        {
            var result = await _client.SendBatchAsync(request, ct);
            if (result != null)
            {
                _outbox.Remove(filePath);
                Interlocked.Add(ref _eventsIngested, result.Succeeded);
            }
        }
    }
}
