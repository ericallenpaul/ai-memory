using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class ProjectRepository : IProjectRepository
{
    private readonly AIMemoryDbContext _db;
    public ProjectRepository(AIMemoryDbContext db) { _db = db; }

    public Task<Project?> GetAsync(string projectId)
        => _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId);

    public Task<Project?> GetByDisplayNameAsync(string displayName)
        => _db.Projects.FirstOrDefaultAsync(p => p.DisplayName == displayName);

    public Task<List<Project>> ListAsync()
        => _db.Projects.OrderByDescending(p => p.LastSeenAt).ToListAsync();

    public async Task<Project> UpsertAsync(Project project)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == project.ProjectId);
        if (existing != null)
        {
            existing.DisplayName = project.DisplayName;
            existing.CanonicalRemoteUrl = project.CanonicalRemoteUrl ?? existing.CanonicalRemoteUrl;
            existing.RootCommitSha = project.RootCommitSha ?? existing.RootCommitSha;
            existing.IdentityKind = project.IdentityKind;
            existing.SourceType = project.SourceType;
            existing.SourcePath = project.SourcePath;
            existing.DefaultBranch = project.DefaultBranch ?? existing.DefaultBranch;
            existing.LastSeenAt = now;
            await _db.SaveChangesAsync();
            return existing;
        }

        project.FirstSeenAt = now;
        project.LastSeenAt = now;
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        return project;
    }
}
