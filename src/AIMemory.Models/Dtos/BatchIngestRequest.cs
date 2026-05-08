namespace AIMemory.Models.Dtos;

public class BatchIngestRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Origin host of this batch — the secondary's <c>host_id</c>. Required for v2 secondaries
    /// (post-phase-7); v1 batches with a null/empty value are treated as the primary's local host
    /// for backcompat. The primary rejects batches whose <c>HostId</c> doesn't correspond to a
    /// known (paired) host with HTTP 403.
    /// </summary>
    public string? HostId { get; set; }

    /// <summary>
    /// Target project for this batch — the canonical <c>project_id</c> resolved by the secondary.
    /// Per-batch, not per-event: every adapter run scans one project at a time. May be null on v1
    /// batches; the legacy code path then resolves the project from <see cref="CodeFileUpsertEvent.RepositoryName"/>.
    /// </summary>
    public string? ProjectId { get; set; }

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
