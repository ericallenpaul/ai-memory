using System.Text.Json;

namespace AIMemory.Models.Events;

public class ArtifactAppendEvent
{
    public string SessionExternalId { get; set; } = string.Empty;
    public string? ArtifactExternalId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? PathOrUrl { get; set; }
    public string? Hash { get; set; }
    public JsonElement? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
