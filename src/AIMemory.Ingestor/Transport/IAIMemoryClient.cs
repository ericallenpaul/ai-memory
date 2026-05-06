using AIMemory.Models.Dtos;

namespace AIMemory.Ingestor.Transport;

public interface IAIMemoryClient
{
    Task<BatchIngestResponse?> SendBatchAsync(BatchIngestRequest request, CancellationToken cancellationToken = default);
}
