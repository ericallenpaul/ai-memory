namespace AIMemory.Models.Entities;

/// <summary>
/// Content-addressed file blob metadata. PK is <see cref="ContentSha256"/> — same content
/// on two hosts produces one <c>CodeFile</c> row, and two <see cref="FileLocation"/> rows.
///
/// <para>Per-host filesystem provenance lives in <see cref="FileLocation"/>; this row only
/// describes the bytes themselves.</para>
/// </summary>
public class CodeFile
{
    public string ContentSha256 { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
