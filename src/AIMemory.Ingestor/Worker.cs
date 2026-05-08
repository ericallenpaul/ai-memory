using System.Text.Json;
using Microsoft.Extensions.Options;
using AIMemory.Identity;
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
    private readonly ILedgerSink _sink;
    private readonly IIngestorContext _ingestorContext;
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
        ILedgerSink sink,
        IIngestorContext ingestorContext,
        OutboxStore outbox,
        IConfiguration appConfig)
    {
        _logger = logger;
        _config = config.Value;
        _adapters = adapters;
        _checkpointStore = checkpointStore;
        _redaction = redaction;
        _sink = sink;
        _ingestorContext = ingestorContext;
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

            // Send in batches. ProjectId is resolved from the file's containing watch path
            // (so the design-doc §3.3 invariant holds — every batch from this file shares one
            // project_id). Non-code adapters that don't supply a project context get a null
            // ProjectId, which the API treats as v1.
            var watchPathForFile = ResolveWatchPath(sourceConfig, filePath);
            var projectId = watchPathForFile != null
                ? _ingestorContext.GetProjectIdentity(watchPathForFile).ProjectId
                : null;

            foreach (var batch in allEvents.Chunk(_config.BatchSize))
            {
                var request = new BatchIngestRequest
                {
                    ClientId = _config.ClientId,
                    MachineName = Environment.MachineName,
                    Source = adapter.SourceName,
                    HostId = _ingestorContext.HostId,
                    ProjectId = projectId,
                    Events = batch.ToList()
                };

                await SendOrQueueAsync(request, batch.Length, ct);
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

            // Group reconcile events by their watch path so each batch carries a uniform
            // project_id. CodeAdapter encodes the watch path inside the delete event's
            // RepositoryName, but for safety we group by the WatchPaths declared in config.
            // For non-code adapters Reconcile is a no-op today, so this branch is code-adapter-only.
            foreach (var watchPath in sourceConfig.WatchPaths)
            {
                var projectId = _ingestorContext.GetProjectIdentity(watchPath).ProjectId;
                var watchPathRepoName = Path.GetFileName(watchPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                var eventsForPath = reconcileEvents
                    .Where(e => MatchesRepository(e, watchPathRepoName))
                    .ToList();
                if (eventsForPath.Count == 0) continue;

                foreach (var batch in eventsForPath.Chunk(_config.BatchSize))
                {
                    var request = new BatchIngestRequest
                    {
                        ClientId = _config.ClientId,
                        MachineName = Environment.MachineName,
                        Source = adapter.SourceName,
                        HostId = _ingestorContext.HostId,
                        ProjectId = projectId,
                        Events = batch.ToList()
                    };

                    await SendOrQueueAsync(request, batch.Length, ct);
                }
            }
            _checkpointStore.Flush();
        }
    }

    /// <summary>
    /// Resolves which configured watch path contains <paramref name="filePath"/>. Picks the
    /// longest match so a nested watch path wins over its parent. Returns null if no watch
    /// path covers the file (defensive — shouldn't happen for code-adapter files).
    /// </summary>
    private static string? ResolveWatchPath(SourceConfig sourceConfig, string filePath) =>
        sourceConfig.WatchPaths
            .Where(wp => filePath.StartsWith(wp, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(wp => wp.Length)
            .FirstOrDefault();

    /// <summary>
    /// Quick filter for grouping reconcile (delete) events by watch path. The CodeAdapter's
    /// delete events stamp <c>RepositoryName</c> with the watch path's folder name; matching
    /// on that lets us bucket without re-walking the file system.
    /// </summary>
    private static bool MatchesRepository(IngestEvent evt, string repoName)
    {
        if (string.IsNullOrEmpty(repoName)) return false;
        try
        {
            using var doc = JsonDocument.Parse(evt.Payload.GetRawText());
            if (doc.RootElement.TryGetProperty("RepositoryName", out var prop)
                || doc.RootElement.TryGetProperty("repositoryName", out prop))
            {
                return string.Equals(prop.GetString(), repoName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // If the payload doesn't parse, leave the event in the bucket-of-last-resort
            // (handled by the loop falling through with no match).
        }
        return false;
    }

    private async Task SendOrQueueAsync(BatchIngestRequest request, int batchEventCount, CancellationToken ct)
    {
        var result = await _sink.SendBatchAsync(request, ct);
        switch (result.Outcome)
        {
            case LedgerSendOutcome.Success when result.Response != null:
                Interlocked.Add(ref _eventsIngested, result.Response.Succeeded);
                Interlocked.Add(ref _eventsFailed, result.Response.Failed);
                break;

            case LedgerSendOutcome.TransientFailure:
                _logger.LogWarning("Sink reported transient failure ({Status}): {Message} — queuing batch",
                    result.HttpStatusCode, result.ErrorMessage);
                _outbox.Enqueue(request);
                Interlocked.Add(ref _eventsFailed, batchEventCount);
                break;

            case LedgerSendOutcome.PermanentFailure:
                // Permanent: don't retry, but still queue so the operator can inspect the outbox
                // file — better than silently dropping events. The next cycle will hit the same
                // permanent failure and surface it again.
                _logger.LogError("Sink reported permanent failure ({Status}): {Message}",
                    result.HttpStatusCode, result.ErrorMessage);
                _outbox.Enqueue(request);
                Interlocked.Add(ref _eventsFailed, batchEventCount);
                break;
        }
    }

    private async Task RetryOutboxAsync(CancellationToken ct)
    {
        if (_outbox.Count == 0) return;

        _logger.LogInformation("Retrying {Count} outbox items", _outbox.Count);

        foreach (var (filePath, request) in _outbox.DequeueAll())
        {
            var result = await _sink.SendBatchAsync(request, ct);
            switch (result.Outcome)
            {
                case LedgerSendOutcome.Success when result.Response != null:
                    _outbox.Remove(filePath);
                    Interlocked.Add(ref _eventsIngested, result.Response.Succeeded);
                    break;

                case LedgerSendOutcome.PermanentFailure:
                    // Don't keep retrying a batch the primary will keep refusing. Leaving it on
                    // disk under a renamed extension lets the operator inspect; for now we just
                    // keep it so the next cycle surfaces the error again. Auto-quarantine is a
                    // future improvement (deferred per design doc §8 — "API key rotation UX").
                    _logger.LogError(
                        "Outbox item {File} permanently rejected ({Status}): {Message}",
                        Path.GetFileName(filePath), result.HttpStatusCode, result.ErrorMessage);
                    break;

                case LedgerSendOutcome.TransientFailure:
                    // Leave on disk; we'll retry next cycle.
                    break;
            }
        }
    }
}
