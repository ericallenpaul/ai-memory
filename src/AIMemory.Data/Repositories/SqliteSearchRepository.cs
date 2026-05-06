using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Dtos;

namespace AIMemory.Data.Repositories;

public class SqliteSearchRepository : ISearchRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<SqliteSearchRepository> _logger;

    public SqliteSearchRepository(AIMemoryDbContext db, ILogger<SqliteSearchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, string? project = null, string? repo = null,
        string? source = null, int limit = 10, int offset = 0)
    {
        _logger.LogInformation("SQLite search for '{Query}' (project={Project}, limit={Limit})", query, project, limit);

        var messagesQuery = _db.Messages
            .Join(_db.Sessions, m => m.SessionId, s => s.SessionId, (m, s) => new { m, s })
            .Where(x => EF.Functions.Like(x.m.Content, $"%{query}%"));

        if (!string.IsNullOrEmpty(project))
            messagesQuery = messagesQuery.Where(x => x.s.Project == project);
        if (!string.IsNullOrEmpty(repo))
            messagesQuery = messagesQuery.Where(x => x.s.Repo == repo);
        if (!string.IsNullOrEmpty(source))
            messagesQuery = messagesQuery.Where(x => x.s.Source == source);

        var results = await messagesQuery
            .OrderByDescending(x => x.m.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(x => new SearchResult
            {
                SessionId = x.m.SessionId,
                SessionTitle = x.s.Title,
                Project = x.s.Project,
                Snippet = x.m.Content.Length > 200 ? x.m.Content.Substring(0, 200) + "..." : x.m.Content,
                Role = x.m.Role,
                Rank = 1.0f,
                CreatedAt = x.m.CreatedAt
            })
            .ToListAsync();

        _logger.LogInformation("SQLite search returned {Count} results", results.Count);
        return results;
    }
}
