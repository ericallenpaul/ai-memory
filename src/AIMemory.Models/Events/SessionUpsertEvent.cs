namespace AIMemory.Models.Events;

public class SessionUpsertEvent
{
    public string SessionExternalId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Project { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<string>? Tags { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string Source { get; set; } = string.Empty;
}
