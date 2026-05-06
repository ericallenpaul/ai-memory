namespace AIMemory.Models.Dtos;

public class MessageResponse
{
    public Guid MessageId { get; set; }
    public Guid SessionId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int? TokenIn { get; set; }
    public int? TokenOut { get; set; }
    public decimal? CostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
