using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Lower-level access to <see cref="FileLocation"/> rows. Most ingest paths go through
/// <see cref="ICodeIndexRepository"/>; this interface exposes raw queries for admin/diagnostic surfaces.
/// </summary>
public interface IFileLocationRepository
{
    Task<List<FileLocation>> ListByProjectAsync(string projectId);
    Task<List<FileLocation>> ListByHostAsync(string hostId);
    Task<List<FileLocation>> ListByContentAsync(string contentSha256);
    Task<FileLocation?> GetAsync(string hostId, string projectId, string relPath);
}
