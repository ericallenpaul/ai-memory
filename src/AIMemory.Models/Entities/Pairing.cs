namespace AIMemory.Models.Entities;

/// <summary>
/// Primary's record of a paired secondary host. Created via <c>POST /api/pairings</c>
/// during the secondary's first-run wizard (phase 7a).
///
/// <para>Active uniqueness is enforced by a partial unique index
/// (<c>WHERE is_revoked = 0</c>) so historical revoked pairings can coexist with one
/// active pairing per host.</para>
/// </summary>
public class Pairing
{
    public Guid PairingId { get; set; }
    public string HostId { get; set; } = string.Empty;
    public Guid ApiKeyId { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastContactAt { get; set; }
    public bool IsRevoked { get; set; }
}
