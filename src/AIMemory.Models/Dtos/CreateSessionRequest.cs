namespace AIMemory.Models.Dtos;

public class CreateSessionRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Project { get; set; }
    public string? Repo { get; set; }
    public string? Branch { get; set; }
    public List<string>? Tags { get; set; }
    public string? Source { get; set; }
    public string? ExternalId { get; set; }
}
