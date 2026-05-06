using System.Text.Json;

namespace AIMemory.Models.Events;

public class MessageAppendEvent
{
    public string SessionExternalId { get; set; } = string.Empty;
    public string? MessageExternalId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int? TokenIn { get; set; }
    public int? TokenOut { get; set; }
    public decimal? CostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public JsonElement? Raw { get; set; }
}
