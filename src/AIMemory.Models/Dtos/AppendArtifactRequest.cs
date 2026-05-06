using System.Text.Json;

namespace AIMemory.Models.Dtos;

public class AppendArtifactRequest
{
    public string Type { get; set; } = string.Empty;
    public string? PathOrUrl { get; set; }
    public string? Hash { get; set; }
    public JsonElement? MetadataJson { get; set; }
    public string? ExternalId { get; set; }
}
