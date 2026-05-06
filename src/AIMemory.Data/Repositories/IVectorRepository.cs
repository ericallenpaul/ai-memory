using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class VectorSearchResult
{
    public Guid MessageId { get; set; }
    public Guid SessionId { get; set; }
    public string? SessionTitle { get; set; }
    public string? Project { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Role { get; set; }
    public double Similarity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public interface IVectorRepository
{
    Task UpsertEmbeddingAsync(MessageEmbedding embedding);
    Task UpsertEmbeddingBatchAsync(IEnumerable<MessageEmbedding> embeddings);
    Task<List<VectorSearchResult>> SearchSimilarAsync(float[] queryEmbedding, int dimensions,
        string? project = null, string? source = null, int limit = 10);
    Task<bool> HasEmbeddingAsync(Guid messageId);
}
