using AIMemory.Ingestor.Transport;
using AIMemory.Models.Dtos;

namespace AIMemory.Tests.Unit.Transport;

/// <summary>
/// Verifies that <see cref="LocalSink"/> faithfully wraps the existing <see cref="IAIMemoryClient"/>:
/// successful sends pass through, null responses become transient failures, exceptions also map
/// to transient. This is the "no behavior change" gate for the local-mode refactor.
/// </summary>
public class LocalSinkTests
{
    private static BatchIngestRequest MakeBatch() => new()
    {
        ClientId = "client-1",
        Source = "code-index",
        Events = [new IngestEvent { Type = "CodeFileUpsert", IdempotencyKey = "k1" }]
    };

    private sealed class StubClient : IAIMemoryClient
    {
        public Func<BatchIngestRequest, CancellationToken, Task<BatchIngestResponse?>>? Behavior { get; set; }
        public int CallCount { get; private set; }
        public BatchIngestRequest? LastRequest { get; private set; }

        public Task<BatchIngestResponse?> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Behavior is not null
                ? Behavior(request, cancellationToken)
                : Task.FromResult<BatchIngestResponse?>(null);
        }
    }

    [Fact]
    public async Task SendBatchAsync_PassesThroughSuccessfulResponse()
    {
        var stub = new StubClient
        {
            Behavior = (_, _) => Task.FromResult<BatchIngestResponse?>(
                new BatchIngestResponse { Total = 1, Succeeded = 1 })
        };
        var sink = new LocalSink(stub);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(1, result.Response!.Succeeded);
    }

    [Fact]
    public async Task SendBatchAsync_NullResponse_BecomesTransient()
    {
        // Mirrors the pre-refactor contract: existing IAIMemoryClient swallows exceptions
        // and returns null. LocalSink translates that into a transient outcome so the worker
        // queues to the outbox just as before.
        var stub = new StubClient { Behavior = (_, _) => Task.FromResult<BatchIngestResponse?>(null) };
        var sink = new LocalSink(stub);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task SendBatchAsync_UnexpectedException_BecomesTransient()
    {
        // The shipped IAIMemoryClient catches exceptions itself, but defense-in-depth: if a
        // future implementation surfaces them, LocalSink shouldn't bring down the worker.
        var stub = new StubClient
        {
            Behavior = (_, _) => throw new InvalidOperationException("boom")
        };
        var sink = new LocalSink(stub);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.TransientFailure, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task SendBatchAsync_PropagatesRequestVerbatim()
    {
        var stub = new StubClient
        {
            Behavior = (_, _) => Task.FromResult<BatchIngestResponse?>(new BatchIngestResponse())
        };
        var sink = new LocalSink(stub);
        var batch = MakeBatch();
        batch.HostId = "host-x";
        batch.ProjectId = "proj-y";

        await sink.SendBatchAsync(batch);

        Assert.Same(batch, stub.LastRequest);
        Assert.Equal("host-x", stub.LastRequest!.HostId);
        Assert.Equal("proj-y", stub.LastRequest.ProjectId);
    }

    [Fact]
    public async Task SendBatchAsync_PropagatesCancellation()
    {
        var stub = new StubClient
        {
            Behavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }
        };
        var sink = new LocalSink(stub);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => sink.SendBatchAsync(MakeBatch(), cts.Token));
    }
}
