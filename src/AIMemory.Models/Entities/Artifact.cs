using System.Text.Json;

namespace AIMemory.Models.Entities;

public class Artifact
{
    public Guid ArtifactId { get; set; }
    public Guid SessionId { get; set; }
    public string? ExternalId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? PathOrUrl { get; set; }
    public string? Hash { get; set; }
    public JsonDocument? MetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Session? Session { get; set; }
}
