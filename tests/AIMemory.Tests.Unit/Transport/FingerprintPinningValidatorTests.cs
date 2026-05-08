using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AIMemory.Ingestor.Transport;

namespace AIMemory.Tests.Unit.Transport;

/// <summary>
/// Unit tests for the certificate validator. Network-level pinning behavior is exercised by
/// <see cref="RemoteSinkTests"/>; this file focuses on the fingerprint comparison logic in
/// isolation.
/// </summary>
public class FingerprintPinningValidatorTests
{
    [Fact]
    public void NormalizeFingerprint_StripsColonsAndLowercases()
    {
        var input = "AB:CD:EF:01:23:45";
        var normalized = FingerprintPinningValidator.NormalizeFingerprint(input);
        Assert.Equal("abcdef012345", normalized);
    }

    [Fact]
    public void NormalizeFingerprint_StripsWhitespaceAndDashes()
    {
        var normalized = FingerprintPinningValidator.NormalizeFingerprint("ab cd-ef 01 23 45");
        Assert.Equal("abcdef012345", normalized);
    }

    [Fact]
    public void Constructor_RejectsShortFingerprint()
    {
        Assert.Throws<ArgumentException>(() => new FingerprintPinningValidator("abcdef"));
    }

    [Fact]
    public void Constructor_RejectsNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => new FingerprintPinningValidator(""));
        Assert.Throws<ArgumentException>(() => new FingerprintPinningValidator("   "));
    }

    [Fact]
    public void ValidateCertificate_ReturnsTrue_WhenFingerprintMatches()
    {
        using var cert = MakeSelfSignedCert("CN=test-primary");
        var fingerprint = FingerprintPinningValidator.ComputeFingerprint(cert);

        var validator = new FingerprintPinningValidator(fingerprint);
        var ok = validator.ValidateCertificate(null, cert, null,
            System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(ok);
    }

    [Fact]
    public void ValidateCertificate_ReturnsFalse_WhenFingerprintDiffers()
    {
        using var pinned = MakeSelfSignedCert("CN=pinned");
        using var different = MakeSelfSignedCert("CN=other");

        var validator = new FingerprintPinningValidator(
            FingerprintPinningValidator.ComputeFingerprint(pinned));
        var ok = validator.ValidateCertificate(null, different, null,
            System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.False(ok);
    }

    [Fact]
    public void ValidateCertificate_ReturnsFalse_WhenCertIsNull()
    {
        var validator = new FingerprintPinningValidator(new string('a', 64));
        var ok = validator.ValidateCertificate(null, null, null,
            System.Net.Security.SslPolicyErrors.None);
        Assert.False(ok);
    }

    [Fact]
    public void ComputeFingerprint_MatchesManualSha256OverDer()
    {
        using var cert = MakeSelfSignedCert("CN=manual");
        var observed = FingerprintPinningValidator.ComputeFingerprint(cert);
        var expected = Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void ValidateCertificate_AcceptsColonSeparatedPin_AfterNormalization()
    {
        using var cert = MakeSelfSignedCert("CN=colons");
        var raw = FingerprintPinningValidator.ComputeFingerprint(cert);
        // Re-format with colons every two chars and uppercase, simulating UI display format.
        var withColons = string.Join(":",
            Enumerable.Range(0, raw.Length / 2).Select(i => raw.Substring(i * 2, 2).ToUpperInvariant()));

        var validator = new FingerprintPinningValidator(withColons);
        var ok = validator.ValidateCertificate(null, cert, null,
            System.Net.Security.SslPolicyErrors.None);
        Assert.True(ok);
    }

    /// <summary>
    /// Builds an in-memory self-signed RSA cert. Same shape the API will generate at runtime.
    /// </summary>
    internal static X509Certificate2 MakeSelfSignedCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());

        // Cert is self-signed; we round-trip it through PFX so RawData reflects the
        // serialized form a real handshake would compare.
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        using var ephemeral = req.CreateSelfSigned(notBefore, notAfter);
        var pfx = ephemeral.Export(X509ContentType.Pfx, "pfx-pwd");
        return X509CertificateLoader.LoadPkcs12(pfx, "pfx-pwd",
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }
}
