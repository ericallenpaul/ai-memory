using AIMemory.Ingestor.Configuration;

namespace AIMemory.Tests.Unit.Configuration;

/// <summary>
/// Confirms the fail-fast behavior of <see cref="IngestorConfigValidator"/>. Distributed
/// brief: "if Mode = remote, all three remote fields must be present; if any is missing,
/// fail fast at startup with a clear error."
/// </summary>
public class IngestorConfigValidatorTests
{
    [Fact]
    public void Validate_LocalMode_DoesNotInspectRemoteSettings()
    {
        var config = new IngestorConfig { Mode = IngestorMode.Local };
        // Remote left empty intentionally — local mode should ignore.
        IngestorConfigValidator.Validate(config);
    }

    [Fact]
    public void Validate_RemoteMode_AcceptsCompleteSettings()
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = "https://primary.lan:5219",
                ApiKey = "aimemory_abc123",
                PinnedCertFingerprint = new string('a', 64)
            }
        };

        IngestorConfigValidator.Validate(config);
    }

    [Fact]
    public void Validate_RemoteMode_AcceptsColonSeparatedFingerprint()
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = "https://primary.lan:5219",
                ApiKey = "aimemory_abc123",
                PinnedCertFingerprint = string.Join(":", Enumerable.Repeat("ab", 32))
            }
        };

        IngestorConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData("", "aimemory_abc123", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://x.lan", "", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("https://x.lan", "aimemory_abc123", "")]
    public void Validate_RemoteMode_FailsFastOnMissingFields(string endpoint, string apiKey, string fingerprint)
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = endpoint,
                ApiKey = apiKey,
                PinnedCertFingerprint = fingerprint
            }
        };

        var ex = Assert.Throws<IngestorConfigurationException>(
            () => IngestorConfigValidator.Validate(config));
        Assert.Contains("Mode=Remote", ex.Message);
    }

    [Fact]
    public void Validate_RemoteMode_RejectsHttpEndpoint()
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = "http://primary.lan:5219",  // not https
                ApiKey = "aimemory_abc123",
                PinnedCertFingerprint = new string('a', 64)
            }
        };

        var ex = Assert.Throws<IngestorConfigurationException>(
            () => IngestorConfigValidator.Validate(config));
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void Validate_RemoteMode_RejectsMalformedFingerprint()
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = "https://primary.lan:5219",
                ApiKey = "aimemory_abc123",
                PinnedCertFingerprint = "not-a-real-fingerprint"
            }
        };

        Assert.Throws<IngestorConfigurationException>(
            () => IngestorConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_RemoteMode_RejectsNonAbsoluteEndpoint()
    {
        var config = new IngestorConfig
        {
            Mode = IngestorMode.Remote,
            Remote = new RemoteSinkConfig
            {
                Endpoint = "/relative-path",
                ApiKey = "aimemory_abc123",
                PinnedCertFingerprint = new string('a', 64)
            }
        };

        Assert.Throws<IngestorConfigurationException>(
            () => IngestorConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => IngestorConfigValidator.Validate(null!));
    }

    [Fact]
    public void IsLikelySha256Fingerprint_AcceptsPlainHex()
    {
        Assert.True(IngestorConfigValidator.IsLikelySha256Fingerprint(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void IsLikelySha256Fingerprint_AcceptsColonHex()
    {
        Assert.True(IngestorConfigValidator.IsLikelySha256Fingerprint(
            "01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef"));
    }

    [Fact]
    public void IsLikelySha256Fingerprint_AcceptsUppercase()
    {
        Assert.True(IngestorConfigValidator.IsLikelySha256Fingerprint(
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!nothex!!")]
    [InlineData("abcd")]   // too short
    public void IsLikelySha256Fingerprint_RejectsInvalid(string? value)
    {
        Assert.False(IngestorConfigValidator.IsLikelySha256Fingerprint(value!));
    }
}
