using System.Net;

namespace AIMemory.Api.Distributed;

/// <summary>
/// Validates and normalizes the user-supplied <c>bindInterface</c> parameter accepted by
/// <c>POST /api/admin/distributed/enable</c>. Encapsulating the rules in one place lets the
/// endpoint stay terse and gives the unit tests a single seam to drive.
///
/// <para>Accepts:</para>
/// <list type="bullet">
///   <item>Any IPv4 address (e.g. <c>192.168.1.50</c>).</item>
///   <item>Wildcard <c>0.0.0.0</c> / <c>::</c> for all interfaces.</item>
///   <item>Loopback (<c>127.0.0.1</c>, <c>::1</c>, or the literal string <c>localhost</c>) — accepted
///   but flagged in <see cref="BindInterfaceValidationResult.IsLoopback"/> so the caller can
///   surface a "this defeats the point of distributed mode" warning.</item>
/// </list>
///
/// <para>Rejects: anything that doesn't parse as <see cref="IPAddress"/>.</para>
/// </summary>
public static class BindInterfaceValidator
{
    /// <summary>
    /// Validates <paramref name="raw"/>. Returns a result indicating success/failure and, on
    /// success, the normalized address string and whether it's a loopback selection.
    /// </summary>
    public static BindInterfaceValidationResult Validate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return BindInterfaceValidationResult.Empty();

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
            trimmed = "127.0.0.1";

        if (!IPAddress.TryParse(trimmed, out var parsed))
        {
            return BindInterfaceValidationResult.Invalid(
                $"Invalid bindInterface '{raw}'. Expected an IPv4 address (e.g. 192.168.1.50), " +
                "0.0.0.0 (all interfaces), or 127.0.0.1 (loopback).");
        }

        return BindInterfaceValidationResult.Valid(parsed.ToString(), IPAddress.IsLoopback(parsed));
    }
}

/// <summary>
/// Result of <see cref="BindInterfaceValidator.Validate"/>. Mutually exclusive shape — a result
/// is one of: empty (no override supplied), valid (with normalized form), or invalid (with reason).
/// </summary>
public sealed class BindInterfaceValidationResult
{
    public bool IsEmpty { get; private init; }
    public bool IsValid { get; private init; }
    public string? NormalizedAddress { get; private init; }
    public bool IsLoopback { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static BindInterfaceValidationResult Empty() => new() { IsEmpty = true };

    public static BindInterfaceValidationResult Valid(string normalized, bool isLoopback) =>
        new() { IsValid = true, NormalizedAddress = normalized, IsLoopback = isLoopback };

    public static BindInterfaceValidationResult Invalid(string error) =>
        new() { IsValid = false, ErrorMessage = error };
}
