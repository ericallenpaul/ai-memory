using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class ContentRepository : IContentRepository
{
    private readonly AIMemoryDbContext _db;
    public ContentRepository(AIMemoryDbContext db) { _db = db; }

    public Task<CodeFile?> GetAsync(string contentSha256)
        => _db.CodeFiles.FirstOrDefaultAsync(f => f.ContentSha256 == contentSha256);

    public Task<List<CodeFile>> GetByHashesAsync(IEnumerable<string> contentSha256s)
    {
        var list = contentSha256s.ToList();
        return _db.CodeFiles.Where(f => list.Contains(f.ContentSha256)).ToListAsync();
    }

    public Task<int> CountAsync() => _db.CodeFiles.CountAsync();
}
