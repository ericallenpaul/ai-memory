namespace AIMemory.Models.Dtos;

public class SearchResult
{
    public Guid SessionId { get; set; }
    public string? SessionTitle { get; set; }
    public string? Project { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public string? Role { get; set; }
    public float Rank { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
