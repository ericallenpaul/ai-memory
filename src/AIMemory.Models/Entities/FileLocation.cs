namespace AIMemory.Models.Entities;

/// <summary>
/// Per-host pointer to where a content blob lives on disk.
///
/// <para>This is the join table that makes "same content on two machines" dedupe correctly
/// (one <see cref="CodeFile"/> row keyed by <c>ContentSha256</c>) while preserving "what does
/// each host have on disk" (multiple <c>FileLocation</c> rows pointing at the same content).</para>
///
/// <para>Composite PK <c>(HostId, ProjectId, RelPath)</c> is the natural idempotency key for
/// ingest events — a re-upload of the same path on the same host is a no-op.</para>
/// </summary>
public class FileLocation
{
    public string HostId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RelPath { get; set; } = string.Empty;       // POSIX-style, project-root relative
    public string ContentSha256 { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
