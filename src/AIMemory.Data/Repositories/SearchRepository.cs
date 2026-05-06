using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Dtos;

namespace AIMemory.Data.Repositories;

public class SearchRepository : ISearchRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<SearchRepository> _logger;

    public SearchRepository(AIMemoryDbContext db, ILogger<SearchRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, string? project = null, string? repo = null,
        string? source = null, int limit = 10, int offset = 0)
    {
        _logger.LogInformation("Searching for '{Query}' (project={Project}, limit={Limit})", query, project, limit);

        var sql = """
            SELECT
                m.session_id AS "SessionId",
                s.title AS "SessionTitle",
                s.project AS "Project",
                ts_headline('english', m.content, plainto_tsquery('english', {0}),
                    'StartSel=>>>, StopSel=<<<, MaxWords=40, MinWords=20') AS "Snippet",
                m.role AS "Role",
                ts_rank(m.content_tsvector, plainto_tsquery('english', {0})) AS "Rank",
                m.created_at AS "CreatedAt"
            FROM messages m
            JOIN sessions s ON m.session_id = s.session_id
            WHERE m.content_tsvector @@ plainto_tsquery('english', {0})
            """;

        var conditions = new List<string>();
        var parameters = new List<object> { query };

        if (!string.IsNullOrEmpty(project))
        {
            conditions.Add($"AND s.project = {{{parameters.Count}}}");
            parameters.Add(project);
        }
        if (!string.IsNullOrEmpty(repo))
        {
            conditions.Add($"AND s.repo = {{{parameters.Count}}}");
            parameters.Add(repo);
        }
        if (!string.IsNullOrEmpty(source))
        {
            conditions.Add($"AND s.source = {{{parameters.Count}}}");
            parameters.Add(source);
        }

        var fullSql = sql + "\n" + string.Join("\n", conditions) +
            $"\nORDER BY \"Rank\" DESC\nLIMIT {limit} OFFSET {offset}";

        var results = await _db.Database
            .SqlQueryRaw<SearchResult>(fullSql, parameters.ToArray())
            .ToListAsync();

        _logger.LogInformation("Search returned {Count} results", results.Count);
        return results;
    }
}
