using AIMemory.Ingestor.Configuration;

namespace AIMemory.Ingestor.Transport;

/// <summary>
/// Builds the <see cref="HttpClient"/> used by <see cref="RemoteSink"/>. Centralized so the
/// fingerprint pinning callback, header wiring, and timeouts all live in one place — and so
/// tests can rebuild the same shape against a test server.
///
/// <para>Not registered with <c>IHttpClientFactory</c> directly because the fingerprint pin
/// is per-config (not per-app) and the typed-client extension doesn't compose with custom
/// handlers as cleanly. We construct it ourselves and register the resulting <c>HttpClient</c>
/// as a singleton against the lifetime of the ingestor host.</para>
/// </summary>
public static class RemoteSinkHttpClientBuilder
{
    /// <summary>Default request timeout — same as the existing local client (30s).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Build an <see cref="HttpClient"/> wired for <see cref="RemoteSink"/>. The returned
    /// client owns its handler — dispose the client to release the socket pool.
    /// </summary>
    /// <param name="config">Validated remote settings — <see cref="IngestorConfigValidator"/>
    /// must have run first.</param>
    /// <param name="handlerOverride">Optional custom <see cref="HttpMessageHandler"/>. Production
    /// passes null and gets a fingerprint-pinned <see cref="HttpClientHandler"/>; tests pass a
    /// <c>TestServer</c>'s handler so they don't need real TLS.</param>
    public static HttpClient Build(RemoteSinkConfig config, HttpMessageHandler? handlerOverride = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var handler = handlerOverride ?? CreatePinnedHandler(config.PinnedCertFingerprint);

        var client = new HttpClient(handler, disposeHandler: handlerOverride is null)
        {
            BaseAddress = new Uri(config.Endpoint),
            Timeout = DefaultTimeout
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            client.DefaultRequestHeaders.Add(RemoteSink.ApiKeyHeaderName, config.ApiKey);

        return client;
    }

    private static HttpClientHandler CreatePinnedHandler(string fingerprint)
    {
        var validator = new FingerprintPinningValidator(fingerprint);
        var handler = new HttpClientHandler
        {
            // We're entirely replacing chain validation with fingerprint pinning. This is
            // intentional — the primary's cert is self-signed, so chain validation would
            // always fail.
            ServerCertificateCustomValidationCallback = validator.ValidateCertificate
        };
        return handler;
    }
}
