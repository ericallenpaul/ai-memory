using System.Security.Cryptography.X509Certificates;
using AIMemory.Api.Tls;

namespace AIMemory.Tests.Unit.Tls;

/// <summary>
/// Phase 7a: cert generation must produce a valid cert with a stable fingerprint across
/// restarts (i.e. the on-disk .pfx is reused, not regenerated on every load).
/// </summary>
public class CertFingerprintTests
{
    [Fact]
    public void Compute_ReturnsLowercaseHexSha256OfDerBytes()
    {
        using var temp = new TempDir();
        var provider = new TlsCertificateProvider(temp.Path);
        using var cert = provider.GetOrCreate();

        var fp = CertFingerprint.Compute(cert);

        Assert.Equal(64, fp.Length); // SHA-256 = 32 bytes = 64 hex chars
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void Compute_ThrowsOnNullCert()
    {
        Assert.Throws<ArgumentNullException>(() => CertFingerprint.Compute(null!));
    }

    [Fact]
    public void ToDisplay_FormatsAsColonSeparatedUppercase()
    {
        // Real cert fingerprints are 64 hex chars; the display form is 32 colon-separated pairs.
        const string wire = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var display = CertFingerprint.ToDisplay(wire);

        Assert.StartsWith("A1:B2:C3:D4:", display);
        // 32 pairs → 31 colons → 95 chars total.
        Assert.Equal(95, display.Length);
        Assert.DoesNotContain("a", display); // all uppercase
    }

    [Fact]
    public void ToDisplay_HandlesEmptyInput()
    {
        Assert.Equal(string.Empty, CertFingerprint.ToDisplay(""));
    }

    [Fact]
    public void TlsCertificateProvider_GeneratesValidSelfSignedCert()
    {
        using var temp = new TempDir();
        var provider = new TlsCertificateProvider(temp.Path);

        using var cert = provider.GetOrCreate();

        Assert.NotNull(cert);
        Assert.Contains("aimemory-primary", cert.Subject);
        // Self-signed: issuer == subject.
        Assert.Equal(cert.Subject, cert.Issuer);
        // Has a private key (HasPrivateKey is the canonical check).
        Assert.True(cert.HasPrivateKey);
        // Pfx file persisted.
        Assert.True(File.Exists(Path.Combine(temp.Path, "tls.pfx")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "tls.pwd")));
    }

    [Fact]
    public void TlsCertificateProvider_FingerprintStableAcrossLoads()
    {
        // First call generates the cert; second call must read it back from disk and produce
        // the same fingerprint. This is the load-bearing property for TOFU pinning — if the
        // fingerprint changed on every restart, every secondary would have to re-pair on
        // every primary restart.
        using var temp = new TempDir();
        var provider1 = new TlsCertificateProvider(temp.Path);
        using var cert1 = provider1.GetOrCreate();
        var fp1 = CertFingerprint.Compute(cert1);

        // Simulate process restart: brand-new provider instance, same disk state.
        var provider2 = new TlsCertificateProvider(temp.Path);
        using var cert2 = provider2.GetOrCreate();
        var fp2 = CertFingerprint.Compute(cert2);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void TlsCertificateProvider_ForceRegenerateChangesFingerprint()
    {
        using var temp = new TempDir();
        var provider = new TlsCertificateProvider(temp.Path);
        using var cert1 = provider.GetOrCreate();
        var fp1 = CertFingerprint.Compute(cert1);

        using var cert2 = provider.ForceRegenerate();
        var fp2 = CertFingerprint.Compute(cert2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void TlsCertificateProvider_IncludesExtraSanHosts()
    {
        using var temp = new TempDir();
        var provider = new TlsCertificateProvider(temp.Path);

        using var cert = provider.GetOrCreate(extraSanHosts: ["10.0.0.5", "primary.lan"]);

        // Find the SAN extension by OID and verify it includes our hosts.
        // Format includes "DNS Name=primary.lan" or "IP Address=10.0.0.5" (formatting varies
        // by .NET version, so we just look for the substrings).
        var sanExt = cert.Extensions["2.5.29.17"];
        Assert.NotNull(sanExt);
        var formatted = sanExt!.Format(true);
        Assert.Contains("primary.lan", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10.0.0.5", formatted);
        // Default SANs are still present.
        Assert.Contains("localhost", formatted, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aimemory-cert-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
