using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class ArtifactRepository : IArtifactRepository
{
    private readonly AIMemoryDbContext _db;
    private readonly ILogger<ArtifactRepository> _logger;

    public ArtifactRepository(AIMemoryDbContext db, ILogger<ArtifactRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Artifact> AppendAsync(Artifact artifact)
    {
        if (artifact.ArtifactId == Guid.Empty)
            artifact.ArtifactId = Guid.NewGuid();
        if (artifact.CreatedAt == default)
            artifact.CreatedAt = DateTimeOffset.UtcNow;

        _db.Artifacts.Add(artifact);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Appended artifact {ArtifactId} ({Type}) to session {SessionId}",
            artifact.ArtifactId, artifact.Type, artifact.SessionId);
        return artifact;
    }

    public async Task<List<Artifact>> GetBySessionAsync(Guid sessionId)
    {
        return await _db.Artifacts
            .Where(a => a.SessionId == sessionId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }
}
