namespace AIMemory.Identity;

/// <summary>
/// Computes the stable per-machine <c>host_id</c> as <c>sha256_hex(machine_guid_bytes || install_salt)</c>.
///
/// <para>Output is lowercase hex, 64 characters. Stable across reboots and OS upgrades;
/// changes on reimage (because the OS-level machine GUID changes).</para>
/// </summary>
public interface IHostIdProvider
{
    /// <summary>
    /// Returns the lowercase-hex SHA-256 host id for this machine.
    /// </summary>
    string GetHostId();

    /// <summary>
    /// Returns the OS kind tag — <c>"windows"</c>, <c>"linux"</c>, or <c>"macos"</c>.
    /// </summary>
    string GetOsKind();
}
