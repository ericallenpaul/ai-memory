namespace AIMemory.Models.Dtos;

public class SessionResponse
{
    public Guid SessionId { get; set; }
    public string? ExternalId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Project { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Source { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int MessageCount { get; set; }
    public List<MessageResponse>? Messages { get; set; }
    public List<ToolCallResponse>? ToolCalls { get; set; }
    public List<ArtifactResponse>? Artifacts { get; set; }
}
