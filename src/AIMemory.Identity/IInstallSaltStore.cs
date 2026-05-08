namespace AIMemory.Identity;

/// <summary>
/// Reads (and lazily creates) the per-install salt used to derive <c>host_id</c>.
///
/// <para>The salt is a 32-byte random value persisted under <c>%ProgramData%\AIMemory\Api</c>
/// (or platform equivalent). It survives MSI/NSIS upgrades because the postinstall hook
/// preserves <c>%ProgramData%\AIMemory</c>.</para>
/// </summary>
public interface IInstallSaltStore
{
    /// <summary>
    /// Returns the persisted salt bytes. Generates a new salt and writes it to disk if no
    /// salt file exists. Throws if the file exists but is unreadable or has the wrong length.
    /// </summary>
    byte[] GetOrCreate();
}
