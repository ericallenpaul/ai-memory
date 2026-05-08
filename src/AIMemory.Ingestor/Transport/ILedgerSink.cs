using AIMemory.Models.Dtos;

namespace AIMemory.Ingestor.Transport;

/// <summary>
/// Abstracts the destination of an ingestor's batch. Two implementations:
/// <list type="bullet">
///   <item><see cref="LocalSink"/> — wraps the existing in-process HTTP path against a primary
///   running on the same machine. Preserves all current behavior.</item>
///   <item><see cref="RemoteSink"/> — HTTPS to a paired primary, with TLS fingerprint pinning
///   and the <c>X-AIMemory-Api-Key</c> header.</item>
/// </list>
///
/// <para>The interface is deliberately semantic, not transport-specific: callers just hand it
/// a <see cref="BatchIngestRequest"/>, the sink returns a <see cref="LedgerSendResult"/>
/// describing what happened, and the worker decides whether to advance checkpoints or queue
/// to the outbox.</para>
/// </summary>
public interface ILedgerSink
{
    /// <summary>
    /// Sends a batch to the configured destination. Returns a structured result rather than
    /// throwing on transport errors — both sinks distinguish transient (retry) from permanent
    /// (drop / surface to user) failures, which the caller needs in order to act correctly.
    /// </summary>
    Task<LedgerSendResult> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an attempted batch send. <see cref="Outcome"/> drives the caller's next move
/// (advance checkpoint, retry, queue, fail).
/// </summary>
public sealed class LedgerSendResult
{
    /// <summary>
    /// What happened with this send.
    /// </summary>
    public LedgerSendOutcome Outcome { get; init; }

    /// <summary>
    /// The primary's structured response. Populated when <see cref="Outcome"/> is
    /// <see cref="LedgerSendOutcome.Success"/>; null otherwise.
    /// </summary>
    public BatchIngestResponse? Response { get; init; }

    /// <summary>
    /// Human-readable error description for non-success outcomes. Logged by the worker.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Underlying exception when relevant. May be null even on failure (e.g. a 4xx HTTP
    /// status that the sink translated into a permanent-failure outcome).
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>HTTP status code when the send reached the wire and got a response. Null otherwise.</summary>
    public int? HttpStatusCode { get; init; }

    public static LedgerSendResult Ok(BatchIngestResponse response) =>
        new() { Outcome = LedgerSendOutcome.Success, Response = response };

    public static LedgerSendResult Transient(string message, Exception? ex = null, int? status = null) =>
        new() { Outcome = LedgerSendOutcome.TransientFailure, ErrorMessage = message, Exception = ex, HttpStatusCode = status };

    public static LedgerSendResult Permanent(string message, Exception? ex = null, int? status = null) =>
        new() { Outcome = LedgerSendOutcome.PermanentFailure, ErrorMessage = message, Exception = ex, HttpStatusCode = status };
}

/// <summary>
/// Outcome categories for <see cref="ILedgerSink.SendBatchAsync"/>.
/// </summary>
public enum LedgerSendOutcome
{
    /// <summary>
    /// Wire round-trip succeeded; <see cref="LedgerSendResult.Response"/> is populated. The
    /// individual events inside may still report partial failures via the response body.
    /// </summary>
    Success,

    /// <summary>
    /// Send failed in a way that's worth retrying — network down, 5xx, timeout, broken pipe,
    /// disk-full at the primary. Caller should queue to the outbox and back off.
    /// </summary>
    TransientFailure,

    /// <summary>
    /// Send failed in a way retrying won't fix — 401 (key revoked), 403 (insufficient scope),
    /// 409 (host conflict), TLS fingerprint mismatch, malformed payload. Caller should surface
    /// the error and stop retrying this batch.
    /// </summary>
    PermanentFailure
}
