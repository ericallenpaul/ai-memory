using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Persistence for <see cref="Project"/> rows. Most code paths use <see cref="ICodeIndexRepository"/>
/// for project access — this interface is the lower-level escape hatch for endpoints that
/// don't care about files/symbols (admin tables, pairings UI).
/// </summary>
public interface IProjectRepository
{
    Task<Project?> GetAsync(string projectId);
    Task<Project?> GetByDisplayNameAsync(string displayName);
    Task<List<Project>> ListAsync();
    Task<Project> UpsertAsync(Project project);
}
