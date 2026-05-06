using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class IngestionRepository : IIngestionRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<IngestionRepository> _logger;

    public IngestionRepository(AIMemoryDbContext db, ILogger<IngestionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string idempotencyKey)
    {
        return await _db.IngestionLog.AnyAsync(e => e.IdempotencyKey == idempotencyKey);
    }

    public async Task<HashSet<string>> FilterExistingKeysAsync(IEnumerable<string> keys)
    {
        var keyList = keys.ToList();
        var existing = await _db.IngestionLog
            .Where(e => keyList.Contains(e.IdempotencyKey))
            .Select(e => e.IdempotencyKey)
            .ToListAsync();

        return existing.ToHashSet();
    }

    public async Task LogAsync(IngestionLogEntry entry)
    {
        entry.CreatedAt = DateTimeOffset.UtcNow;
        _db.IngestionLog.Add(entry);
        await _db.SaveChangesAsync();
    }

    public async Task LogBatchAsync(IEnumerable<IngestionLogEntry> entries)
    {
        foreach (var entry in entries)
            entry.CreatedAt = DateTimeOffset.UtcNow;

        _db.IngestionLog.AddRange(entries);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Logged {Count} ingestion entries", entries.Count());
    }

    public async Task<List<IngestionLogEntry>> ListAsync(string? source = null, string? eventType = null,
        int limit = 50, int offset = 0)
    {
        var query = _db.IngestionLog.AsQueryable();

        if (!string.IsNullOrEmpty(source))
            query = query.Where(e => e.Source == source);
        if (!string.IsNullOrEmpty(eventType))
            query = query.Where(e => e.EventType == eventType);

        return await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }
}
