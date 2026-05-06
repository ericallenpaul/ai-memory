using System.Text.Json;

namespace AIMemory.Models.Dtos;

public class ToolCallResponse
{
    public Guid ToolCallId { get; set; }
    public Guid SessionId { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public JsonDocument? ArgumentsJson { get; set; }
    public JsonDocument? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
