namespace AIMemory.Models.Entities;

public class Session
{
    public Guid SessionId { get; set; }
    public string? ExternalId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Project { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Source { get; set; }
    public string? MachineName { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Message> Messages { get; set; } = [];
    public List<ToolCall> ToolCalls { get; set; } = [];
    public List<Artifact> Artifacts { get; set; } = [];
}
