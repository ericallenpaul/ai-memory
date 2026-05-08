using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AIMemory.Ingestor.Transport;

/// <summary>
/// Wire-time TLS validator for <see cref="RemoteSink"/>. The .NET HTTP stack hands every
/// presented leaf cert to this callback; we ignore the chain (self-signed certs would fail
/// hostname validation) and pin on the SHA-256 fingerprint of the DER bytes.
///
/// <para>This is the secondary half of the TOFU pin established during the pairing wizard:
/// the wizard captures the fingerprint when the user pastes it; this class enforces it on
/// every subsequent connection.</para>
/// </summary>
public sealed class FingerprintPinningValidator
{
    private readonly string _expectedNormalized;

    public FingerprintPinningValidator(string expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
            throw new ArgumentException("expectedFingerprint must be non-empty", nameof(expectedFingerprint));

        _expectedNormalized = NormalizeFingerprint(expectedFingerprint);
        if (_expectedNormalized.Length != 64)
        {
            throw new ArgumentException(
                "expectedFingerprint must be a SHA-256 hex string (64 hex chars after stripping colons)",
                nameof(expectedFingerprint));
        }
    }

    /// <summary>
    /// The fingerprint we'll compare against, normalized to lowercase hex with no separators.
    /// </summary>
    public string ExpectedFingerprint => _expectedNormalized;

    /// <summary>
    /// Strips colons and whitespace and lowercases the input. Accepts any of:
    /// <c>"a1b2..."</c>, <c>"A1:B2:..."</c>, <c>"a1 b2 ..."</c>.
    /// </summary>
    public static string NormalizeFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return fingerprint
            .Replace(":", "")
            .Replace(" ", "")
            .Replace("-", "")
            .Trim()
            .ToLowerInvariant();
    }

    /// <summary>
    /// Computes the SHA-256 of <paramref name="cert"/>'s DER bytes and returns it as 64-char
    /// lowercase hex. Matches the format the API's <c>CertFingerprint</c> emits.
    /// </summary>
    public static string ComputeFingerprint(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Custom certificate validation entry point — matches the signature
    /// <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c> expects.
    /// </summary>
    public bool ValidateCertificate(
        HttpRequestMessage? _,
        X509Certificate2? cert,
        X509Chain? __,
        System.Net.Security.SslPolicyErrors ___)
    {
        if (cert is null) return false;
        var observed = ComputeFingerprint(cert);
        return string.Equals(observed, _expectedNormalized, StringComparison.Ordinal);
    }
}
