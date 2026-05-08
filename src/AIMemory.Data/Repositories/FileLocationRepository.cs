using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class FileLocationRepository : IFileLocationRepository
{
    private readonly AIMemoryDbContext _db;
    public FileLocationRepository(AIMemoryDbContext db) { _db = db; }

    public Task<List<FileLocation>> ListByProjectAsync(string projectId)
        => _db.FileLocations.Where(l => l.ProjectId == projectId).OrderBy(l => l.RelPath).ToListAsync();

    public Task<List<FileLocation>> ListByHostAsync(string hostId)
        => _db.FileLocations.Where(l => l.HostId == hostId).OrderBy(l => l.ProjectId).ThenBy(l => l.RelPath).ToListAsync();

    public Task<List<FileLocation>> ListByContentAsync(string contentSha256)
        => _db.FileLocations.Where(l => l.ContentSha256 == contentSha256).ToListAsync();

    public Task<FileLocation?> GetAsync(string hostId, string projectId, string relPath)
        => _db.FileLocations.FirstOrDefaultAsync(l =>
            l.HostId == hostId && l.ProjectId == projectId && l.RelPath == relPath);
}
