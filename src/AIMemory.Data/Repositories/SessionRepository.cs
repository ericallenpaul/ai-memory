using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class SessionRepository : ISessionRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<SessionRepository> _logger;

    public SessionRepository(AIMemoryDbContext db, ILogger<SessionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Session> CreateAsync(Session session)
    {
        if (session.SessionId == Guid.Empty)
            session.SessionId = Guid.NewGuid();
        session.CreatedAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Created session {SessionId} - {Title}", session.SessionId, session.Title);
        return session;
    }

    public async Task<Session?> GetByIdAsync(Guid sessionId)
    {
        return await _db.Sessions.FindAsync(sessionId);
    }

    public async Task<Session?> GetByExternalIdAsync(string externalId)
    {
        return await _db.Sessions.FirstOrDefaultAsync(s => s.ExternalId == externalId);
    }

    public async Task<Session> UpsertByExternalIdAsync(Session session)
    {
        var existing = !string.IsNullOrEmpty(session.ExternalId)
            ? await GetByExternalIdAsync(session.ExternalId)
            : null;

        if (existing != null)
        {
            existing.Title = session.Title.Length > 0 ? session.Title : existing.Title;
            existing.Project = session.Project ?? existing.Project;
            existing.Repo = session.Repo ?? existing.Repo;
            existing.Branch = session.Branch ?? existing.Branch;
            existing.Source = session.Source ?? existing.Source;
            if (session.Tags.Count > 0) existing.Tags = session.Tags;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync();
            _logger.LogInformation("Upserted (updated) session {SessionId}", existing.SessionId);
            return existing;
        }

        return await CreateAsync(session);
    }

    public async Task<Session> UpdateAsync(Session session)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _db.Sessions.Update(session);
        await _db.SaveChangesAsync();
        return session;
    }

    public async Task<List<Session>> ListAsync(string? project = null, string? repo = null, string? source = null,
        string? tag = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = 50, int offset = 0)
    {
        var query = _db.Sessions.AsQueryable();

        if (!string.IsNullOrEmpty(project))
            query = query.Where(s => s.Project == project);
        if (!string.IsNullOrEmpty(repo))
            query = query.Where(s => s.Repo == repo);
        if (!string.IsNullOrEmpty(source))
            query = query.Where(s => s.Source == source);
        if (from.HasValue)
            query = query.Where(s => s.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(s => s.CreatedAt <= to.Value);

        // Tag filtering is done in memory since storage format varies by provider
        var results = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(offset)
            .Take(string.IsNullOrEmpty(tag) ? limit : limit * 3) // overfetch if filtering by tag
            .ToListAsync();

        if (!string.IsNullOrEmpty(tag))
        {
            results = results
                .Where(s => s.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Take(limit)
                .ToList();
        }

        return results;
    }

    public async Task<int> CountAsync()
    {
        return await _db.Sessions.CountAsync();
    }
}
