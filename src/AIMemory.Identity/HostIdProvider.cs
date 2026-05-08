using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace AIMemory.Identity;

/// <summary>
/// Reads the OS-level machine GUID and combines it with the per-install salt to produce
/// a privacy-preserving stable host id. See design doc §2.1.
/// </summary>
public sealed class HostIdProvider : IHostIdProvider
{
    private readonly IInstallSaltStore _saltStore;
    private readonly IMachineGuidReader _machineGuidReader;
    private readonly ILogger<HostIdProvider> _logger;
    private string? _cached;
    private readonly object _lock = new();

    public HostIdProvider(IInstallSaltStore saltStore, IMachineGuidReader? machineGuidReader = null, ILogger<HostIdProvider>? logger = null)
    {
        _saltStore = saltStore ?? throw new ArgumentNullException(nameof(saltStore));
        _machineGuidReader = machineGuidReader ?? new DefaultMachineGuidReader();
        _logger = logger ?? NullLogger<HostIdProvider>.Instance;
    }

    public string GetHostId()
    {
        if (_cached != null) return _cached;
        lock (_lock)
        {
            if (_cached != null) return _cached;

            var guidBytes = _machineGuidReader.ReadBytes();
            var salt = _saltStore.GetOrCreate();

            var combined = new byte[guidBytes.Length + salt.Length];
            Buffer.BlockCopy(guidBytes, 0, combined, 0, guidBytes.Length);
            Buffer.BlockCopy(salt, 0, combined, guidBytes.Length, salt.Length);

            var hash = SHA256.HashData(combined);
            _cached = Convert.ToHexStringLower(hash);
            return _cached;
        }
    }

    public string GetOsKind()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }
}

/// <summary>
/// Reads the raw OS machine GUID. Abstracted for testing — production wiring uses
/// <see cref="DefaultMachineGuidReader"/>; tests inject a fixed-bytes implementation.
/// </summary>
public interface IMachineGuidReader
{
    /// <summary>
    /// Returns a stable per-machine identifier as a UTF-8 byte array.
    /// On Windows: <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>.
    /// On Linux: <c>/etc/machine-id</c> or <c>/var/lib/dbus/machine-id</c>.
    /// On macOS: <c>IOPlatformUUID</c> via <c>ioreg</c>.
    /// </summary>
    byte[] ReadBytes();
}

/// <summary>
/// Default platform-aware machine GUID reader. Falls back to <see cref="Environment.MachineName"/>
/// hashed bytes if the OS-specific path is unavailable — degrades gracefully on misconfigured
/// systems rather than throwing.
/// </summary>
public sealed class DefaultMachineGuidReader : IMachineGuidReader
{
    public byte[] ReadBytes()
    {
        try
        {
            string? raw = OperatingSystem.IsWindows() ? ReadWindows()
                        : OperatingSystem.IsLinux() ? ReadLinux()
                        : OperatingSystem.IsMacOS() ? ReadMacOs()
                        : null;

            if (!string.IsNullOrWhiteSpace(raw))
                return Encoding.UTF8.GetBytes(raw.Trim());
        }
        catch
        {
            // fall through to fallback
        }

        // Fallback: hostname. Stable enough that host_id stays consistent across runs on a
        // single machine, even if the OS path is missing.
        return Encoding.UTF8.GetBytes(Environment.MachineName);
    }

#pragma warning disable CA1416 // Validate platform compatibility — guarded by OperatingSystem.IsWindows()
    private static string? ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }
#pragma warning restore CA1416

    private static string? ReadLinux()
    {
        const string systemd = "/etc/machine-id";
        const string dbus = "/var/lib/dbus/machine-id";
        if (File.Exists(systemd)) return File.ReadAllText(systemd);
        if (File.Exists(dbus)) return File.ReadAllText(dbus);
        return null;
    }

    private static string? ReadMacOs()
    {
        // ioreg -d2 -c IOPlatformExpertDevice | awk -F\" '/IOPlatformUUID/{print $(NF-1)}'
        var psi = new ProcessStartInfo("ioreg", "-d2 -c IOPlatformExpertDevice")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);

        const string marker = "\"IOPlatformUUID\"";
        var idx = output.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var rest = output[(idx + marker.Length)..];
        var first = rest.IndexOf('"');
        if (first < 0) return null;
        var second = rest.IndexOf('"', first + 1);
        if (second < 0) return null;
        return rest.Substring(first + 1, second - first - 1);
    }
}
