using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Dtos;

namespace AIMemory.Ingestor.Transport;

public class AIMemoryHttpClient : IAIMemoryClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AIMemoryHttpClient> _logger;

    public AIMemoryHttpClient(HttpClient httpClient, ILogger<AIMemoryHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<BatchIngestResponse?> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Sending batch of {Count} events to AIMemory API", request.Events.Count);

            var response = await _httpClient.PostAsJsonAsync("/api/ingest/batch", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<BatchIngestResponse>(cancellationToken);
            _logger.LogInformation("Batch result: {Succeeded} ok, {Duplicates} duplicates, {Failed} failed",
                result?.Succeeded, result?.Duplicates, result?.Failed);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send batch to AIMemory API");
            return null;
        }
    }
}
