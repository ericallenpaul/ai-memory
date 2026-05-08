namespace AIMemory.Ingestor.Configuration;

/// <summary>
/// Fail-fast configuration validation for <see cref="IngestorConfig"/>.
///
/// <para>Run at startup before the host transitions to <c>Run()</c>. A misconfigured
/// remote-mode ingestor should refuse to start rather than silently misbehave at the first
/// batch — the brief calls this out as a hard requirement.</para>
/// </summary>
public static class IngestorConfigValidator
{
    /// <summary>
    /// Validates the supplied config. Throws <see cref="IngestorConfigurationException"/>
    /// if a required field is missing or invalid.
    /// </summary>
    public static void Validate(IngestorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Mode == IngestorMode.Remote)
            ValidateRemote(config.Remote);
    }

    private static void ValidateRemote(RemoteSinkConfig remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(remote.Endpoint)) missing.Add(nameof(remote.Endpoint));
        if (string.IsNullOrWhiteSpace(remote.ApiKey)) missing.Add(nameof(remote.ApiKey));
        if (string.IsNullOrWhiteSpace(remote.PinnedCertFingerprint)) missing.Add(nameof(remote.PinnedCertFingerprint));

        if (missing.Count > 0)
        {
            throw new IngestorConfigurationException(
                $"Mode=Remote requires Ingestor.Remote.{string.Join(", Ingestor.Remote.", missing)} to be set. " +
                "Re-run the pairing wizard on this machine to populate them.");
        }

        if (!Uri.TryCreate(remote.Endpoint, UriKind.Absolute, out var uri))
        {
            throw new IngestorConfigurationException(
                $"Ingestor.Remote.Endpoint '{remote.Endpoint}' is not a valid absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new IngestorConfigurationException(
                $"Ingestor.Remote.Endpoint must use https; got '{uri.Scheme}'. Remote mode is TLS-only.");
        }

        if (!IsLikelySha256Fingerprint(remote.PinnedCertFingerprint))
        {
            throw new IngestorConfigurationException(
                "Ingestor.Remote.PinnedCertFingerprint must be a SHA-256 hex string (64 hex chars, " +
                "with optional colons between bytes).");
        }
    }

    /// <summary>
    /// Cheap shape check: 64 lowercase hex chars after stripping colons. The actual fingerprint
    /// match against the wire cert happens later in <c>FingerprintPinningValidator</c>.
    /// </summary>
    public static bool IsLikelySha256Fingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Replace(":", "").Trim();
        if (trimmed.Length != 64) return false;
        foreach (var c in trimmed)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }
}

/// <summary>
/// Thrown when the ingestor's configuration is invalid for the selected mode. Hosts startup
/// must propagate this as a fatal error so service control sees a non-zero exit and the user
/// gets a clear message instead of a silent restart loop.
/// </summary>
public sealed class IngestorConfigurationException : Exception
{
    public IngestorConfigurationException(string message) : base(message) { }
    public IngestorConfigurationException(string message, Exception inner) : base(message, inner) { }
}
