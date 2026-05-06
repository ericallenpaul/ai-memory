using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface ISessionRepository
{
    Task<Session> CreateAsync(Session session);
    Task<Session?> GetByIdAsync(Guid sessionId);
    Task<Session?> GetByExternalIdAsync(string externalId);
    Task<Session> UpsertByExternalIdAsync(Session session);
    Task<Session> UpdateAsync(Session session);
    Task<List<Session>> ListAsync(string? project = null, string? repo = null, string? source = null,
        string? tag = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = 50, int offset = 0);
    Task<int> CountAsync();
}
