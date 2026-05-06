using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface IIngestionRepository
{
    Task<bool> ExistsAsync(string idempotencyKey);
    Task<HashSet<string>> FilterExistingKeysAsync(IEnumerable<string> keys);
    Task LogAsync(IngestionLogEntry entry);
    Task LogBatchAsync(IEnumerable<IngestionLogEntry> entries);
    Task<List<IngestionLogEntry>> ListAsync(string? source = null, string? eventType = null,
        int limit = 50, int offset = 0);
}
