using System.Net;
using System.Net.Http.Json;
using AIMemory.Models.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Ingestor.Transport;

/// <summary>
/// <see cref="ILedgerSink"/> for distributed deployments. POSTs the batch over HTTPS to the
/// paired primary's <c>/api/ingest/batch</c> endpoint. TLS is validated by SHA-256 fingerprint
/// pinning (set via <see cref="FingerprintPinningValidator"/> on the underlying handler) — the
/// primary's self-signed cert would otherwise fail standard chain validation.
///
/// <para>Auth header: <c>X-AIMemory-Api-Key</c> (the new name agreed for phase 7a). The
/// legacy <c>X-API-Key</c> is no longer emitted by this sink.</para>
///
/// <para>Retry policy (design doc §6 + brief): up to 5 attempts with exponential backoff +
/// full jitter (1, 2, 4, 8, 16 seconds). Permanent statuses (401, 403, 409, 422, 4xx) skip
/// retry and surface the failure immediately so the worker can stop ingesting.</para>
/// </summary>
public sealed class RemoteSink : ILedgerSink
{
    /// <summary>
    /// Header name used for the ingestor's API key. Phase 7a is migrating the API to accept
    /// this header (with <c>X-API-Key</c> as a backcompat alias); RemoteSink only emits the
    /// new name.
    /// </summary>
    public const string ApiKeyHeaderName = "X-AIMemory-Api-Key";

    private static readonly TimeSpan[] DefaultBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16)
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<RemoteSink> _logger;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly Random _jitter = new();

    /// <summary>
    /// Construct a remote sink against the supplied <see cref="HttpClient"/>. The caller is
    /// expected to have configured the client's <c>BaseAddress</c>, default headers (including
    /// <c>X-AIMemory-Api-Key</c>), and a fingerprint-pinning <c>HttpMessageHandler</c>. In DI
    /// this is wired up by <c>RemoteSinkHttpFactory</c>.
    /// </summary>
    public RemoteSink(HttpClient httpClient, ILogger<RemoteSink>? logger = null, IReadOnlyList<TimeSpan>? backoffSchedule = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<RemoteSink>.Instance;
        _backoff = backoffSchedule ?? DefaultBackoff;
    }

    public async Task<LedgerSendResult> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxAttempts = _backoff.Count;
        Exception? lastException = null;
        int? lastStatus = null;
        string? lastError = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await _httpClient
                    .PostAsJsonAsync("/api/ingest/batch", request, cancellationToken)
                    .ConfigureAwait(false);

                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content
                        .ReadFromJsonAsync<BatchIngestResponse>(cancellationToken)
                        .ConfigureAwait(false);

                    if (body == null)
                    {
                        _logger.LogWarning("RemoteSink: server returned 2xx with empty body — retrying");
                        lastError = "empty response body";
                        await DelayBeforeRetryAsync(attempt, cancellationToken);
                        continue;
                    }

                    return LedgerSendResult.Ok(body);
                }

                if (IsPermanent(response.StatusCode))
                {
                    var detail = await SafeReadBodyAsync(response, cancellationToken);
                    var msg = $"remote primary returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}";
                    _logger.LogError("RemoteSink: permanent failure — {Message}", msg);
                    return LedgerSendResult.Permanent(msg, status: (int)response.StatusCode);
                }

                lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.LogWarning(
                    "RemoteSink: transient failure on attempt {Attempt}/{Max} — {Status}",
                    attempt + 1, maxAttempts, lastError);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsFingerprintPinningFailure(ex))
            {
                // Pinning failures are permanent — different leaf cert, possible MITM, must re-pair.
                _logger.LogError(ex, "RemoteSink: TLS fingerprint mismatch — refusing connection");
                return LedgerSendResult.Permanent(
                    "TLS fingerprint mismatch — the primary's certificate doesn't match the pinned " +
                    "fingerprint. This may indicate a compromised network or that the primary has " +
                    "rotated its cert; re-run the pairing wizard.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                lastError = ex.Message;
                _logger.LogWarning(
                    ex, "RemoteSink: transport failure on attempt {Attempt}/{Max}",
                    attempt + 1, maxAttempts);
            }
            catch (TaskCanceledException ex)
            {
                lastException = ex;
                lastError = "request timed out";
                _logger.LogWarning(ex, "RemoteSink: timeout on attempt {Attempt}/{Max}", attempt + 1, maxAttempts);
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }

        return LedgerSendResult.Transient(
            $"remote send exhausted {maxAttempts} attempts: {lastError ?? "unknown"}",
            lastException,
            lastStatus);
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        if (attempt + 1 >= _backoff.Count) return;

        // Full jitter: pick a random delay in [0, backoff[attempt]].
        var ceiling = _backoff[attempt];
        var jitterMs = _jitter.Next(0, (int)Math.Max(1, ceiling.TotalMilliseconds));
        await Task.Delay(TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);
    }

    private static bool IsPermanent(HttpStatusCode status)
    {
        var code = (int)status;
        // 4xx codes that won't fix themselves on retry. 408 (timeout) and 429 (rate limit)
        // are intentionally excluded — those are transient.
        if (code == 408 || code == 429) return false;
        return code >= 400 && code < 500;
    }

    private static bool IsFingerprintPinningFailure(HttpRequestException ex)
    {
        // The .NET HTTP stack wraps a callback-rejection in HttpRequestException with an
        // AuthenticationException inner. We only treat fingerprint failures as permanent —
        // generic "remote certificate is invalid according to the validation procedure" maps
        // to the same code path.
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is System.Security.Authentication.AuthenticationException) return true;
            inner = inner.InnerException;
        }
        return false;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var s = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Cap body in error messages so a chatty primary doesn't blow up our log line.
            return s.Length > 512 ? s[..512] + "…" : s;
        }
        catch
        {
            return "<unreadable body>";
        }
    }
}
