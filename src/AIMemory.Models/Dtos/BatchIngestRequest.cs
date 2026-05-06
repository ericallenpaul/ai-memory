namespace AIMemory.Models.Dtos;

public class BatchIngestRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<IngestEvent> Events { get; set; } = [];
}

public class IngestEvent
{
    public string Type { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public System.Text.Json.JsonElement Payload { get; set; }
}

public class BatchIngestResponse
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Duplicates { get; set; }
    public int Failed { get; set; }
    public List<IngestEventResult> Results { get; set; } = [];
}

public class IngestEventResult
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}
