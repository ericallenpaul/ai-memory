using System.Text.Json;
using Microsoft.Extensions.Logging;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Models.Dtos;
using AIMemory.Models.Events;

namespace AIMemory.Ingestor.Adapters;

public class ClaudeCodeAdapter : ISourceAdapter
{
    private readonly ILogger<ClaudeCodeAdapter> _logger;

    public ClaudeCodeAdapter(ILogger<ClaudeCodeAdapter> logger)
    {
        _logger = logger;
    }

    public string SourceName => "claude-code";

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
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw.Line);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Failed to parse JSONL line at offset {Offset} in {File}", raw.Offset, raw.FilePath);
            yield break;
        }

        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            yield break;

        var type = typeEl.GetString();

        // Skip internal Claude Code types
        if (type == "file-history-snapshot")
            yield break;

        var idempotencyKey = $"{raw.Source}|{raw.FilePath}|{raw.Offset}";

        // Extract session info
        var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
        if (string.IsNullOrEmpty(sessionId))
            yield break;

        var timestamp = root.TryGetProperty("timestamp", out var ts) && ts.GetString() is string tsStr
            ? DateTimeOffset.Parse(tsStr)
            : DateTimeOffset.UtcNow;

        // Emit SessionUpsert for every record (API will deduplicate)
        var cwd = root.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
        var gitBranch = root.TryGetProperty("gitBranch", out var brEl) ? brEl.GetString() : null;
        var project = cwd != null ? Path.GetFileName(cwd) : null;

        yield return new IngestEvent
        {
            Type = "SessionUpsert",
            IdempotencyKey = $"{raw.Source}|session|{sessionId}",
            Payload = JsonSerializer.SerializeToElement(new SessionUpsertEvent
            {
                SessionExternalId = sessionId,
                Title = project ?? "Claude Code Session",
                Project = project,
                Repo = project,
                Branch = gitBranch,
                Source = SourceName,
                StartedAt = timestamp,
                Tags = [raw.Segment]
            })
        };

        // Parse message content
        if (root.TryGetProperty("message", out var msgEl))
        {
            var role = msgEl.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? "user" : "user";
            var content = ExtractContent(msgEl);
            var uuid = root.TryGetProperty("uuid", out var uuidEl) ? uuidEl.GetString() : null;

            if (!string.IsNullOrEmpty(content))
            {
                yield return new IngestEvent
                {
                    Type = "MessageAppend",
                    IdempotencyKey = idempotencyKey,
                    Payload = JsonSerializer.SerializeToElement(new MessageAppendEvent
                    {
                        SessionExternalId = sessionId,
                        MessageExternalId = uuid,
                        Role = role,
                        Content = content,
                        CreatedAt = timestamp,
                        Provider = "anthropic"
                    })
                };
            }
        }
    }

    private static string ExtractContent(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("content", out var contentEl))
            return string.Empty;

        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? string.Empty;

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in contentEl.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textEl))
                    parts.Add(textEl.GetString() ?? "");
                else if (item.ValueKind == JsonValueKind.String)
                    parts.Add(item.GetString() ?? "");
            }
            return string.Join("\n", parts);
        }

        return string.Empty;
    }
}
