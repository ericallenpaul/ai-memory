namespace AIMemory.Models.Dtos;

public class AppendMessageRequest
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? RequestId { get; set; }
    public int? TokenIn { get; set; }
    public int? TokenOut { get; set; }
    public decimal? CostUsd { get; set; }
    public int? LatencyMs { get; set; }
    public string? ExternalId { get; set; }
}
