using System.Text.Json;

namespace AIMemory.Models.Events;

public class ToolCallAppendEvent
{
    public string SessionExternalId { get; set; } = string.Empty;
    public string? ToolCallExternalId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public JsonElement? ArgumentsJson { get; set; }
    public JsonElement? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public JsonElement? Raw { get; set; }
}
