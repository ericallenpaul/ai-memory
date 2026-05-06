using System.Text.Json;

namespace AIMemory.Models.Dtos;

public class AppendToolCallRequest
{
    public string ToolName { get; set; } = string.Empty;
    public JsonElement? ArgumentsJson { get; set; }
    public JsonElement? ResultJson { get; set; }
    public string? ExternalId { get; set; }
}
