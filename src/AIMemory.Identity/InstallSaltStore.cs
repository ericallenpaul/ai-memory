using System.Security.Cryptography;

namespace AIMemory.Identity;

/// <summary>
/// File-backed implementation of <see cref="IInstallSaltStore"/>. The salt file is
/// 32 bytes of cryptographically random data. Created on first call, never rotated.
///
/// <para>Path is configurable so tests can run against a temp dir.</para>
/// </summary>
public sealed class InstallSaltStore : IInstallSaltStore
{
    public const int SaltLengthBytes = 32;
    public const string DefaultFileName = "install-salt.bin";

    private readonly string _path;
    private readonly object _lock = new();
    private byte[]? _cached;

    /// <summary>
    /// Constructs a store rooted at the given directory. The file is named
    /// <c>install-salt.bin</c> inside that directory. Directory is created if missing.
    /// </summary>
    public InstallSaltStore(string directory)
        : this(Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), DefaultFileName), null)
    {
    }

    private InstallSaltStore(string path, object? _)
    {
        _path = path;
    }

    /// <summary>
    /// Builds a store at the platform-default location:
    /// <list type="bullet">
    /// <item>Windows: <c>%ProgramData%\AIMemory\Api\install-salt.bin</c></item>
    /// <item>Linux/macOS: <c>/var/lib/aimemory/install-salt.bin</c> if writable, else
    /// <c>$XDG_DATA_HOME/aimemory/install-salt.bin</c> or <c>~/.local/share/aimemory</c>.</item>
    /// </list>
    /// </summary>
    public static InstallSaltStore CreateDefault()
    {
        return new InstallSaltStore(GetDefaultDirectory());
    }

    public static string GetDefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "AIMemory", "Api");
        }

        // Linux/macOS — prefer /var/lib if writable for service installs.
        const string varLib = "/var/lib/aimemory";
        try
        {
            Directory.CreateDirectory(varLib);
            return varLib;
        }
        catch
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "aimemory");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", "aimemory");
        }
    }

    public byte[] GetOrCreate()
    {
        if (_cached != null)
            return _cached;

        lock (_lock)
        {
            if (_cached != null)
                return _cached;

            if (File.Exists(_path))
            {
                var bytes = File.ReadAllBytes(_path);
                if (bytes.Length != SaltLengthBytes)
                {
                    throw new InvalidDataException(
                        $"Install salt at '{_path}' has wrong length: expected {SaltLengthBytes}, got {bytes.Length}. " +
                        "Delete the file to regenerate (note: this changes host_id).");
                }
                _cached = bytes;
                return _cached;
            }

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var fresh = RandomNumberGenerator.GetBytes(SaltLengthBytes);
            File.WriteAllBytes(_path, fresh);
            _cached = fresh;
            return _cached;
        }
    }
}
