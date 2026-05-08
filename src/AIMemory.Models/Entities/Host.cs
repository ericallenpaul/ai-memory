namespace AIMemory.Models.Entities;

/// <summary>
/// A machine that has ingested into this primary. The local machine is row #1
/// (<see cref="IsLocal"/> = true).
///
/// <para><c>HostId</c> is a 64-character lowercase hex SHA-256 derived from the OS
/// machine GUID + a per-install salt. See <c>AIMemory.Identity.HostIdProvider</c>.</para>
/// </summary>
public class Host
{
    public string HostId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string OsKind { get; set; } = string.Empty;   // "windows" | "linux" | "macos"
    public bool IsLocal { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
