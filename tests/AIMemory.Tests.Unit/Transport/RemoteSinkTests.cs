using System.Net;
using System.Net.Http.Json;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Transport;
using AIMemory.Models.Dtos;

namespace AIMemory.Tests.Unit.Transport;

/// <summary>
/// In-process tests for <see cref="RemoteSink"/>. We don't spin up a real HTTPS server —
/// transport correctness comes from <see cref="FingerprintPinningValidatorTests"/> and the
/// design doc TLS rules. Here we verify:
///
/// <list type="bullet">
///   <item>Successful 200 → <see cref="LedgerSendOutcome.Success"/> with parsed body.</item>
///   <item>Permanent 4xx (e.g. 401, 403, 409) → <see cref="LedgerSendOutcome.PermanentFailure"/>; no retry.</item>
///   <item>Transient 5xx (or transport throws) → retried up to the configured cap, then
///   surfaces as <see cref="LedgerSendOutcome.TransientFailure"/>.</item>
///   <item>The configured API key is sent under <c>X-AIMemory-Api-Key</c>.</item>
///   <item>Batches are POSTed to <c>/api/ingest/batch</c> with the request body intact.</item>
/// </list>
/// </summary>
public class RemoteSinkTests
{
    private static readonly TimeSpan[] FastBackoff =
    [
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1)
    ];

    private static BatchIngestRequest MakeBatch() => new()
    {
        ClientId = "client-1",
        Source = "code-index",
        HostId = "host-abc",
        ProjectId = "proj-xyz",
        Events = [
            new IngestEvent
            {
                Type = "CodeFileUpsert",
                IdempotencyKey = "k1",
                // Default-constructed JsonElement isn't serializable; wrap an empty object so
                // System.Text.Json can write it.
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { })
            }
        ]
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Behavior { get; set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Behavior is null)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return await Behavior(request, cancellationToken);
        }
    }

    private static HttpClient BuildClient(FakeHandler handler, string apiKey = "ingest-key-123")
    {
        return RemoteSinkHttpClientBuilder.Build(
            new RemoteSinkConfig
            {
                Endpoint = "https://primary.lan:5219",
                ApiKey = apiKey,
                PinnedCertFingerprint = new string('a', 64)  // not exercised — handler intercepts
            },
            handlerOverride: handler);
    }

    [Fact]
    public async Task SendBatchAsync_Success_ReturnsParsedResponse()
    {
        var handler = new FakeHandler
        {
            Behavior = (_, _) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new BatchIngestResponse { Total = 1, Succeeded = 1 })
                };
                return Task.FromResult(resp);
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(1, result.Response!.Succeeded);
        Assert.Single(handler.Requests);
        var sent = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/ingest/batch", sent.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SendBatchAsync_AttachesApiKeyHeader()
    {
        var handler = new FakeHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new BatchIngestResponse())
            })
        };
        var sink = new RemoteSink(BuildClient(handler, apiKey: "secret-xyz"), backoffSchedule: FastBackoff);

        await sink.SendBatchAsync(MakeBatch());

        var sent = handler.Requests[0];
        Assert.True(sent.Headers.TryGetValues(RemoteSink.ApiKeyHeaderName, out var values));
        Assert.Equal("secret-xyz", values!.Single());
        // And the legacy X-API-Key header is NOT sent, per phase 7a's hard-cut.
        Assert.False(sent.Headers.Contains("X-API-Key"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SendBatchAsync_PermanentStatus_FailsImmediately(HttpStatusCode status)
    {
        var handler = new FakeHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("rejected")
            })
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal((int)status, result.HttpStatusCode);
        // Permanent statuses skip retry — only one attempt should reach the wire.
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    [InlineData(HttpStatusCode.RequestTimeout)]   // 408 — explicitly transient
    [InlineData((HttpStatusCode)429)]             // 429 — rate limit, transient
    public async Task SendBatchAsync_TransientStatus_RetriesUpToCap(HttpStatusCode status)
    {
        var handler = new FakeHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(status))
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(FastBackoff.Length, handler.Requests.Count);
    }

    [Fact]
    public async Task SendBatchAsync_TransportException_RetriesAndSurfacesTransient()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Behavior = (_, _) =>
            {
                calls++;
                throw new HttpRequestException("connection refused");
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.TransientFailure, result.Outcome);
        Assert.Equal(FastBackoff.Length, calls);
        Assert.IsType<HttpRequestException>(result.Exception);
    }

    [Fact]
    public async Task SendBatchAsync_FingerprintPinningFailure_IsPermanent()
    {
        // The .NET stack signals pin/cert validation failure by wrapping an
        // AuthenticationException inside HttpRequestException. We don't get a real TLS
        // handshake here, so we synthesize that exact shape.
        var handler = new FakeHandler
        {
            Behavior = (_, _) =>
            {
                var inner = new System.Security.Authentication.AuthenticationException(
                    "The remote certificate is invalid according to the validation procedure.");
                throw new HttpRequestException("TLS error", inner);
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.PermanentFailure, result.Outcome);
        Assert.Contains("fingerprint", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendBatchAsync_RetriesAndEventuallySucceeds()
    {
        var calls = 0;
        var handler = new FakeHandler
        {
            Behavior = (_, _) =>
            {
                calls++;
                if (calls < 2)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new BatchIngestResponse { Total = 1, Succeeded = 1 })
                });
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var result = await sink.SendBatchAsync(MakeBatch());

        Assert.Equal(LedgerSendOutcome.Success, result.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(1, result.Response!.Succeeded);
    }

    [Fact]
    public async Task SendBatchAsync_SerializesBatchBody_IncludingHostAndProjectIds()
    {
        BatchIngestRequest? received = null;
        var handler = new FakeHandler
        {
            Behavior = async (req, ct) =>
            {
                received = await req.Content!.ReadFromJsonAsync<BatchIngestRequest>(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new BatchIngestResponse())
                };
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        var batch = MakeBatch();
        await sink.SendBatchAsync(batch);

        Assert.NotNull(received);
        Assert.Equal("host-abc", received!.HostId);
        Assert.Equal("proj-xyz", received.ProjectId);
        Assert.Single(received.Events);
        Assert.Equal("CodeFileUpsert", received.Events[0].Type);
    }

    [Fact]
    public async Task SendBatchAsync_PropagatesCancellation()
    {
        var handler = new FakeHandler
        {
            Behavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        var sink = new RemoteSink(BuildClient(handler), backoffSchedule: FastBackoff);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.SendBatchAsync(MakeBatch(), cts.Token));
    }
}
