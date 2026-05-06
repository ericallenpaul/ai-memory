using System.Text.Json;

namespace AIMemory.Models.Entities;

public class ToolCall
{
    public Guid ToolCallId { get; set; }
    public Guid SessionId { get; set; }
    public string? ExternalId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public JsonDocument? ArgumentsJson { get; set; }
    public JsonDocument? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Session? Session { get; set; }
}
