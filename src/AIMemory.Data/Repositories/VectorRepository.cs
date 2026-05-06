using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Database-agnostic vector repository. Stores embeddings as byte[] (BLOB) and computes
/// cosine similarity in application code. Works identically on both SQLite and PostgreSQL.
/// </summary>
public class VectorRepository : IVectorRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<VectorRepository> _logger;

    public VectorRepository(AIMemoryDbContext db, ILogger<VectorRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task UpsertEmbeddingAsync(MessageEmbedding embedding)
    {
        var existing = await _db.MessageEmbeddings.FindAsync(embedding.MessageId);
        if (existing != null)
        {
            existing.Embedding = embedding.Embedding;
            existing.Dimensions = embedding.Dimensions;
            existing.Model = embedding.Model;
            existing.CreatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            embedding.CreatedAt = DateTimeOffset.UtcNow;
            _db.MessageEmbeddings.Add(embedding);
        }
        await _db.SaveChangesAsync();
    }

    public async Task UpsertEmbeddingBatchAsync(IEnumerable<MessageEmbedding> embeddings)
    {
        foreach (var embedding in embeddings)
        {
            var existing = await _db.MessageEmbeddings.FindAsync(embedding.MessageId);
            if (existing != null)
            {
                existing.Embedding = embedding.Embedding;
                existing.Dimensions = embedding.Dimensions;
                existing.Model = embedding.Model;
                existing.CreatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                embedding.CreatedAt = DateTimeOffset.UtcNow;
                _db.MessageEmbeddings.Add(embedding);
            }
        }
        await _db.SaveChangesAsync();
    }

    public async Task<List<VectorSearchResult>> SearchSimilarAsync(float[] queryEmbedding, int dimensions,
        string? project = null, string? source = null, int limit = 10)
    {
        _logger.LogInformation("Vector search with {Dimensions}d embedding (project={Project}, limit={Limit})",
            dimensions, project, limit);

        // Load candidate embeddings with session/message data
        var query = _db.MessageEmbeddings
            .Join(_db.Messages, e => e.MessageId, m => m.MessageId, (e, m) => new { e, m })
            .Join(_db.Sessions, x => x.e.SessionId, s => s.SessionId, (x, s) => new { x.e, x.m, s })
            .Where(x => x.e.Dimensions == dimensions);

        if (!string.IsNullOrEmpty(project))
            query = query.Where(x => x.s.Project == project);
        if (!string.IsNullOrEmpty(source))
            query = query.Where(x => x.s.Source == source);

        var candidates = await query
            .Select(x => new
            {
                x.e.MessageId,
                x.e.SessionId,
                x.s.Title,
                x.s.Project,
                x.m.Content,
                x.m.Role,
                x.m.CreatedAt,
                x.e.Embedding
            })
            .ToListAsync();

        // Compute cosine similarity in memory
        var results = candidates
            .Select(c => new VectorSearchResult
            {
                MessageId = c.MessageId,
                SessionId = c.SessionId,
                SessionTitle = c.Title,
                Project = c.Project,
                Content = c.Content.Length > 300 ? c.Content[..300] + "..." : c.Content,
                Role = c.Role,
                Similarity = CosineSimilarity(queryEmbedding, BytesToFloats(c.Embedding)),
                CreatedAt = c.CreatedAt
            })
            .OrderByDescending(r => r.Similarity)
            .Take(limit)
            .ToList();

        _logger.LogInformation("Vector search returned {Count} results", results.Count);
        return results;
    }

    public async Task<bool> HasEmbeddingAsync(Guid messageId)
    {
        return await _db.MessageEmbeddings.AnyAsync(e => e.MessageId == messageId);
    }

    public static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        for (int i = 0; i < floats.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), floats[i]);
        }
        return bytes;
    }

    public static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        }
        return floats;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        double dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }
}
