using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AIMemory.Api.Tls;

/// <summary>
/// Generates and persists the self-signed cert used for the HTTPS listener when distributed
/// mode is enabled. Cert lives at <c>%ProgramData%\AIMemory\Api\tls.pfx</c>; password lives
/// alongside in <c>tls.pwd</c>. See design doc §3.6.
///
/// <para>Cert rotation is out of scope for phase 7a — the file is created once, never
/// auto-rotated. Manual rotation = delete the .pfx, restart the API, re-pair secondaries.</para>
/// </summary>
public sealed class TlsCertificateProvider
{
    public const string PfxFileName = "tls.pfx";
    public const string PwdFileName = "tls.pwd";
    private const int ValidityDays = 825; // Apple's max — matches design §3.6.

    private readonly string _pfxPath;
    private readonly string _pwdPath;
    private readonly object _lock = new();

    public string PfxPath => _pfxPath;

    public TlsCertificateProvider(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _pfxPath = Path.Combine(baseDirectory, PfxFileName);
        _pwdPath = Path.Combine(baseDirectory, PwdFileName);
    }

    /// <summary>
    /// Returns the existing cert, or generates a fresh self-signed one if no .pfx is present.
    /// SANs include localhost / 127.0.0.1 / ::1 / the machine hostname / additional bind hosts.
    /// </summary>
    public X509Certificate2 GetOrCreate(IEnumerable<string>? extraSanHosts = null)
    {
        lock (_lock)
        {
            if (File.Exists(_pfxPath) && File.Exists(_pwdPath))
            {
                var pwd = File.ReadAllText(_pwdPath).Trim();
                // X509KeyStorageFlags.Exportable so the fingerprint can be recomputed and the
                // cert can be persisted back if the pipeline ever re-encrypts it.
                return X509CertificateLoader.LoadPkcs12FromFile(
                    _pfxPath, pwd, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            return Generate(extraSanHosts);
        }
    }

    /// <summary>
    /// Forces creation of a fresh cert, overwriting any existing .pfx. Used by
    /// <c>POST /api/admin/distributed/enable</c> when the user explicitly wants to rotate.
    /// </summary>
    public X509Certificate2 ForceRegenerate(IEnumerable<string>? extraSanHosts = null)
    {
        lock (_lock)
        {
            return Generate(extraSanHosts);
        }
    }

    private X509Certificate2 Generate(IEnumerable<string>? extraSanHosts)
    {
        var dir = Path.GetDirectoryName(_pfxPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=aimemory-primary",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Basic constraints + key usage suitable for a TLS server cert.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        var seenDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IPAddress.Loopback.ToString(),
            IPAddress.IPv6Loopback.ToString(),
        };

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName) && seenDns.Add(hostName))
                sanBuilder.AddDnsName(hostName);
        }
        catch
        {
            // GetHostName can fail in odd environments; cert is still usable for loopback.
        }

        if (extraSanHosts != null)
        {
            foreach (var host in extraSanHosts)
            {
                if (string.IsNullOrWhiteSpace(host)) continue;
                if (IPAddress.TryParse(host, out var ip))
                {
                    if (seenIps.Add(ip.ToString()))
                        sanBuilder.AddIpAddress(ip);
                }
                else if (seenDns.Add(host))
                {
                    sanBuilder.AddDnsName(host);
                }
            }
        }

        req.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // small clock-skew cushion
        var notAfter = notBefore.AddDays(ValidityDays);
        using var cert = req.CreateSelfSigned(notBefore, notAfter);

        // The default keyset isn't persistable cross-process, so re-import via Pkcs12.
        var password = GenerateRandomPassword();
        var pfxBytes = cert.Export(X509ContentType.Pkcs12, password);

        File.WriteAllBytes(_pfxPath, pfxBytes);
        File.WriteAllText(_pwdPath, password);
        TryRestrictAcls(_pfxPath);
        TryRestrictAcls(_pwdPath);

        return X509CertificateLoader.LoadPkcs12(
            pfxBytes, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static string GenerateRandomPassword()
    {
        // 32 bytes of random → 64 hex chars, well above any reasonable PFX KDF threshold.
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static void TryRestrictAcls(string path)
    {
        // Best-effort. On Windows we rely on the file inheriting %ProgramData%\AIMemory ACLs
        // (LocalSystem + Administrators); on Linux/macOS the parent dir's perms govern access.
        // Stricter ACL hardening is a deferred phase-7+ item.
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Ignore — best-effort hardening.
        }
    }
}
