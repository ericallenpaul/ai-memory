using AIMemory.Models.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Ingestor.Transport;

/// <summary>
/// <see cref="ILedgerSink"/> for single-machine deployments. Today this delegates to the
/// existing <see cref="IAIMemoryClient"/> (which posts to a local-loopback API), preserving
/// the pre-Phase-7b behavior bit-for-bit. The abstraction is in place so a future phase can
/// swap to direct repository writes (design doc §10) without changing callers.
///
/// <para>Translation rules:</para>
/// <list type="bullet">
///   <item>Successful HTTP response → <see cref="LedgerSendOutcome.Success"/>.</item>
///   <item>Underlying client returns null (network/exception) → <see cref="LedgerSendOutcome.TransientFailure"/>,
///   so the worker queues the batch to the outbox just like before.</item>
/// </list>
/// </summary>
public sealed class LocalSink : ILedgerSink
{
    private readonly IAIMemoryClient _client;
    private readonly ILogger<LocalSink> _logger;

    public LocalSink(IAIMemoryClient client, ILogger<LocalSink>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<LocalSink>.Instance;
    }

    public async Task<LedgerSendResult> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await _client.SendBatchAsync(request, cancellationToken).ConfigureAwait(false);
            if (response == null)
            {
                _logger.LogWarning("LocalSink: underlying client returned null — treating as transient failure");
                return LedgerSendResult.Transient("local API client returned no response");
            }

            return LedgerSendResult.Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LocalSink: unexpected exception forwarding batch");
            return LedgerSendResult.Transient("local sink threw", ex);
        }
    }
}
