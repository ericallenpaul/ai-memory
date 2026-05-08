namespace AIMemory.Models.Dtos;

/// <summary>
/// Body of <c>POST /api/pairings</c>. The secondary submits its host_id and metadata so the
/// primary can record the pairing and bind subsequent ingest batches to a known host. See
/// design doc §3.2 / §4.1.
/// </summary>
public class CreatePairingRequest
{
    /// <summary>64-char lowercase hex sha-256 — the secondary's <c>host_id</c>.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>User-facing label, e.g. "eric-laptop". Must be unique on the primary;
    /// collisions with other hosts are surfaced via 409.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>"windows" | "linux" | "macos".</summary>
    public string OsKind { get; set; } = string.Empty;

    /// <summary>Optional. The ingestor binary version that will be running on this host.</summary>
    public string? IngestorVersion { get; set; }
}

/// <summary>
/// Response of <c>POST /api/pairings</c>. Returned with HTTP 201.
/// </summary>
public class PairingResponse
{
    public Guid PairingId { get; set; }
    public string HostId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastContactAt { get; set; }
    public bool IsRevoked { get; set; }
}
