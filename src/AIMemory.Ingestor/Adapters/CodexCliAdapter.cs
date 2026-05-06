using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Models.Dtos;
using AIMemory.Models.Events;

namespace AIMemory.Ingestor.Adapters;

public partial class CodexCliAdapter : ISourceAdapter
{
    private readonly ILogger<CodexCliAdapter> _logger;

    public CodexCliAdapter(ILogger<CodexCliAdapter> logger)
    {
        _logger = logger;
    }

    public string SourceName => "codex-cli";

    public IEnumerable<string> DiscoverFiles(SourceConfig config)
    {
        foreach (var watchPath in config.WatchPaths)
        {
            if (!Directory.Exists(watchPath)) continue;

            foreach (var pattern in config.FilePatterns)
            {
                foreach (var file in Directory.EnumerateFiles(watchPath, pattern, SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    public IEnumerable<RawRecord> ReadNewRecords(string filePath, Checkpoint? checkpoint)
    {
        if (!File.Exists(filePath)) yield break;

        long startOffset = checkpoint?.LastProcessedOffset ?? 0;
        var fileInfo = new FileInfo(filePath);

        if (checkpoint != null && fileInfo.Length < checkpoint.FileSize)
        {
            _logger.LogWarning("File {File} appears truncated/rotated, resetting offset", filePath);
            startOffset = 0;
        }

        long currentOffset = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            if (currentOffset >= startOffset && !string.IsNullOrWhiteSpace(line))
            {
                yield return new RawRecord
                {
                    FilePath = filePath,
                    Offset = currentOffset,
                    Line = line,
                    Source = SourceName
                };
            }
            currentOffset++;
        }
    }

    public IEnumerable<IngestEvent> ParseRecord(RawRecord raw)
    {
        var idempotencyKey = $"{raw.Source}|{raw.FilePath}|{raw.Offset}";

        // Try JSONL (history.jsonl)
        if (raw.FilePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var evt in ParseJsonlRecord(raw, idempotencyKey))
                yield return evt;
            yield break;
        }

        // Try log format (codex-tui.log)
        if (raw.FilePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var evt in ParseLogRecord(raw, idempotencyKey))
                yield return evt;
        }
    }

    private IEnumerable<IngestEvent> ParseJsonlRecord(RawRecord raw, string idempotencyKey)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw.Line);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Failed to parse JSONL at offset {Offset} in {File}", raw.Offset, raw.FilePath);
            yield break;
        }

        var root = doc.RootElement;

        var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
        if (string.IsNullOrEmpty(sessionId)) yield break;

        var ts = root.TryGetProperty("ts", out var tsEl) ? tsEl.GetInt64() : 0;
        var timestamp = ts > 0
            ? DateTimeOffset.FromUnixTimeSeconds(ts)
            : DateTimeOffset.UtcNow;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;
        if (string.IsNullOrEmpty(text)) yield break;

        yield return new IngestEvent
        {
            Type = "SessionUpsert",
            IdempotencyKey = $"{raw.Source}|session|{sessionId}",
            Payload = JsonSerializer.SerializeToElement(new SessionUpsertEvent
            {
                SessionExternalId = sessionId,
                Title = "Codex CLI Session",
                Source = SourceName,
                StartedAt = timestamp,
                Tags = [raw.Segment]
            })
        };

        yield return new IngestEvent
        {
            Type = "MessageAppend",
            IdempotencyKey = idempotencyKey,
            Payload = JsonSerializer.SerializeToElement(new MessageAppendEvent
            {
                SessionExternalId = sessionId,
                Role = "user",
                Content = text,
                CreatedAt = timestamp,
                Provider = "openai"
            })
        };
    }

    private IEnumerable<IngestEvent> ParseLogRecord(RawRecord raw, string idempotencyKey)
    {
        var match = ToolCallLogRegex().Match(raw.Line);
        if (!match.Success) yield break;

        var timestampStr = match.Groups[1].Value;
        var toolName = match.Groups[2].Value;
        var argsJson = match.Groups[3].Value;

        if (!DateTimeOffset.TryParse(timestampStr, out var timestamp))
            timestamp = DateTimeOffset.UtcNow;

        JsonElement? args = null;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(argsJson);
        }
        catch { /* best effort */ }

        yield return new IngestEvent
        {
            Type = "ToolCallAppend",
            IdempotencyKey = idempotencyKey,
            Payload = JsonSerializer.SerializeToElement(new ToolCallAppendEvent
            {
                SessionExternalId = "codex-tui-log",
                ToolName = toolName,
                ArgumentsJson = args,
                CreatedAt = timestamp
            })
        };
    }

    [GeneratedRegex(@"^(\S+)\s+INFO\s+ToolCall:\s+(\S+)\s+(.+)$")]
    private static partial Regex ToolCallLogRegex();
}
