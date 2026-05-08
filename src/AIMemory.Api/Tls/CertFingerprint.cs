using System.Security.Cryptography.X509Certificates;

namespace AIMemory.Api.Tls;

/// <summary>
/// Computes the SHA-256 fingerprint of an X.509 certificate's DER bytes — the value the
/// secondary pins after pairing (TOFU). See design doc §3.6.
/// </summary>
public static class CertFingerprint
{
    /// <summary>
    /// Returns the lowercase-hex SHA-256 of the cert's DER bytes (no <c>0x</c>, no separators).
    /// This is what flows on the wire; UI surfaces typically render it as colon-separated
    /// uppercase pairs.
    /// </summary>
    public static string Compute(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        // RawData is the DER-encoded form. Use the static SHA-256 hash helper.
        var hash = System.Security.Cryptography.SHA256.HashData(cert.RawData);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Renders a wire-format fingerprint as the colon-separated uppercase form typically
    /// shown in UIs. Example: <c>"a1b2c3..." → "A1:B2:C3:..."</c>.
    /// </summary>
    public static string ToDisplay(string wireFingerprint)
    {
        if (string.IsNullOrEmpty(wireFingerprint)) return string.Empty;
        var upper = wireFingerprint.ToUpperInvariant();
        var sb = new System.Text.StringBuilder(upper.Length + upper.Length / 2);
        for (var i = 0; i < upper.Length; i += 2)
        {
            if (i > 0) sb.Append(':');
            sb.Append(upper, i, Math.Min(2, upper.Length - i));
        }
        return sb.ToString();
    }
}
