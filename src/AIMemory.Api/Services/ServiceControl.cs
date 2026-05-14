using System.Diagnostics;
using System.Runtime.Versioning;

namespace AIMemory.Api.Services;

public enum ServiceState { Running, Stopped, StartPending, StopPending, NotInstalled, Unknown }

public sealed record ServiceStatus(string Name, ServiceState State, int? Pid, string? DisplayName);

public sealed record ServiceOperationResult(bool Success, string? Error);

/// <summary>
/// Cross-platform service control. Windows uses <c>sc.exe</c> via process invocation
/// (no extra NuGet dependency); Linux/macOS are unsupported until a systemd shell-out
/// is wired in. The Tauri shell that this replaces was Windows-only for the same reasons
/// — services.rs in apps/desktop/src-tauri had matching stub returns on non-Windows.
///
/// Only a fixed whitelist of service names is accepted (<c>aimemory-api</c>,
/// <c>aimemory-ingestor</c>) so the endpoint can't be abused to start/stop arbitrary
/// system services even if auth is bypassed somehow.
/// </summary>
public sealed class ServiceControl
{
    public static readonly IReadOnlySet<string> AllowedNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aimemory-api", "aimemory-ingestor" };

    private readonly ILogger<ServiceControl> _logger;

    public ServiceControl(ILogger<ServiceControl> logger) => _logger = logger;

    public bool IsAllowed(string name) => AllowedNames.Contains(name);

    public async Task<ServiceStatus> GetStatusAsync(string name, CancellationToken ct = default)
    {
        EnsureAllowed(name);
        if (OperatingSystem.IsWindows()) return await GetStatusWindowsAsync(name, ct);
        return new ServiceStatus(name, ServiceState.Unknown, null, null);
    }

    public async Task<ServiceOperationResult> StartAsync(string name, CancellationToken ct = default)
    {
        EnsureAllowed(name);
        if (OperatingSystem.IsWindows()) return await ScCommandAsync("start", name, ct);
        return Unsupported();
    }

    public async Task<ServiceOperationResult> StopAsync(string name, CancellationToken ct = default)
    {
        EnsureAllowed(name);
        if (OperatingSystem.IsWindows()) return await ScCommandAsync("stop", name, ct);
        return Unsupported();
    }

    public async Task<ServiceOperationResult> RestartAsync(string name, CancellationToken ct = default)
    {
        EnsureAllowed(name);
        if (!OperatingSystem.IsWindows()) return Unsupported();

        var stop = await ScCommandAsync("stop", name, ct);
        // Tolerate "service is not currently started" — the caller asked for restart, so a
        // service in Stopped state is a no-op for stop, not an error.
        if (!stop.Success && !(stop.Error ?? "").Contains("not started", StringComparison.OrdinalIgnoreCase)
            && !(stop.Error ?? "").Contains("1062", StringComparison.Ordinal))
        {
            return stop;
        }

        // SCM transitions through STOP_PENDING; matches Tauri's 1500ms pause.
        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);

        return await ScCommandAsync("start", name, ct);
    }

    private void EnsureAllowed(string name)
    {
        if (!IsAllowed(name))
            throw new ArgumentException($"Service '{name}' is not in the allowed list", nameof(name));
    }

    private static ServiceOperationResult Unsupported() =>
        new(false, "Service control is not implemented on this platform");

    [SupportedOSPlatform("windows")]
    private async Task<ServiceStatus> GetStatusWindowsAsync(string name, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync("sc.exe", ["query", name], ct);

        // sc.exe returns 1060 when the service is not installed.
        if (exitCode == 1060 || stderr.Contains("1060") || stdout.Contains("1060"))
            return new ServiceStatus(name, ServiceState.NotInstalled, null, null);

        if (exitCode != 0)
        {
            _logger.LogWarning("sc query {Name} failed (exit {Exit}): {Stderr}", name, exitCode, stderr);
            return new ServiceStatus(name, ServiceState.Unknown, null, null);
        }

        var state = ParseScQueryState(stdout);
        var pid = ParseScQueryPid(stdout);
        var displayName = await GetDisplayNameWindowsAsync(name, ct);
        return new ServiceStatus(name, state, pid, displayName);
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> GetDisplayNameWindowsAsync(string name, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunAsync("sc.exe", ["qc", name], ct);
        if (exitCode != 0) return null;
        // Line shape: "DISPLAY_NAME       : AIMemory API"
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("DISPLAY_NAME", StringComparison.OrdinalIgnoreCase))
            {
                var colon = t.IndexOf(':');
                if (colon > 0 && colon < t.Length - 1) return t[(colon + 1)..].Trim();
            }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private async Task<ServiceOperationResult> ScCommandAsync(string verb, string name, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunAsync("sc.exe", [verb, name], ct);
        if (exitCode == 0) return new(true, null);

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        _logger.LogWarning("sc {Verb} {Name} failed (exit {Exit}): {Detail}", verb, name, exitCode, detail);
        return new(false, $"sc {verb} exited {exitCode}: {detail.Trim()}");
    }

    public static ServiceState ParseScQueryState(string scQueryOutput)
    {
        // sc.exe query output includes a line like:
        //     STATE              : 4  RUNNING
        // We pull the numeric code which is stable across locales.
        foreach (var line in scQueryOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("STATE", StringComparison.OrdinalIgnoreCase)) continue;
            var colon = t.IndexOf(':');
            if (colon < 0) continue;
            var rest = t[(colon + 1)..].Trim();
            var firstToken = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(firstToken, out var code))
            {
                return code switch
                {
                    1 => ServiceState.Stopped,
                    2 => ServiceState.StartPending,
                    3 => ServiceState.StopPending,
                    4 => ServiceState.Running,
                    _ => ServiceState.Unknown
                };
            }
        }
        return ServiceState.Unknown;
    }

    public static int? ParseScQueryPid(string scQueryOutput)
    {
        // PID line only appears when the service is running:
        //     PID                : 1234
        foreach (var line in scQueryOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("PID", StringComparison.OrdinalIgnoreCase)) continue;
            var colon = t.IndexOf(':');
            if (colon < 0) continue;
            if (int.TryParse(t[(colon + 1)..].Trim(), out var pid) && pid > 0) return pid;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
