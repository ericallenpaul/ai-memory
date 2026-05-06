using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class CodeIndexRepository : ICodeIndexRepository
{
    private readonly AIMemoryDbContext _db;

    public CodeIndexRepository(AIMemoryDbContext db)
    {
        _db = db;
    }

    public async Task<CodeRepository> UpsertRepositoryAsync(CodeRepository repo)
    {
        var existing = await _db.CodeRepositories.FirstOrDefaultAsync(r => r.Name == repo.Name);
        if (existing != null)
        {
            existing.SourceType = repo.SourceType;
            existing.SourcePath = repo.SourcePath;
            existing.DefaultBranch = repo.DefaultBranch;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            repo.RepositoryId = Guid.NewGuid();
            repo.IndexedAt = DateTimeOffset.UtcNow;
            repo.UpdatedAt = DateTimeOffset.UtcNow;
            _db.CodeRepositories.Add(repo);
            existing = repo;
        }

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task<CodeRepository?> GetRepositoryAsync(Guid id)
    {
        return await _db.CodeRepositories.FirstOrDefaultAsync(r => r.RepositoryId == id);
    }

    public async Task<CodeRepository?> GetRepositoryByNameAsync(string name)
    {
        return await _db.CodeRepositories.FirstOrDefaultAsync(r => r.Name == name);
    }

    public async Task<List<CodeRepository>> ListRepositoriesAsync()
    {
        return await _db.CodeRepositories.OrderByDescending(r => r.UpdatedAt).ToListAsync();
    }

    public async Task DeleteRepositoryAsync(Guid id)
    {
        var symbols = _db.CodeSymbols.Where(s => s.RepositoryId == id);
        _db.CodeSymbols.RemoveRange(symbols);

        var files = _db.CodeFiles.Where(f => f.RepositoryId == id);
        _db.CodeFiles.RemoveRange(files);

        var repo = await _db.CodeRepositories.FindAsync(id);
        if (repo != null)
            _db.CodeRepositories.Remove(repo);

        await _db.SaveChangesAsync();
    }

    public async Task UpsertFileAsync(CodeFile file)
    {
        var existing = await _db.CodeFiles
            .FirstOrDefaultAsync(f => f.RepositoryId == file.RepositoryId && f.FilePath == file.FilePath);

        if (existing != null)
        {
            existing.Language = file.Language;
            existing.FileSize = file.FileSize;
            existing.ContentHash = file.ContentHash;
            existing.IndexedAt = DateTimeOffset.UtcNow;
            file.FileId = existing.FileId;
        }
        else
        {
            file.FileId = Guid.NewGuid();
            file.IndexedAt = DateTimeOffset.UtcNow;
            _db.CodeFiles.Add(file);
        }

        await _db.SaveChangesAsync();
    }

    public async Task DeleteStaleFilesAsync(Guid repositoryId, IEnumerable<string> currentFilePaths)
    {
        var pathSet = currentFilePaths.ToHashSet();
        var staleFiles = await _db.CodeFiles
            .Where(f => f.RepositoryId == repositoryId)
            .ToListAsync();

        var toRemove = staleFiles.Where(f => !pathSet.Contains(f.FilePath)).ToList();
        if (toRemove.Count == 0) return;

        var staleFileIds = toRemove.Select(f => f.FileId).ToHashSet();
        var staleSymbols = _db.CodeSymbols.Where(s => staleFileIds.Contains(s.FileId));
        _db.CodeSymbols.RemoveRange(staleSymbols);
        _db.CodeFiles.RemoveRange(toRemove);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> DeleteFileAsync(Guid repositoryId, string filePath)
    {
        var file = await _db.CodeFiles
            .FirstOrDefaultAsync(f => f.RepositoryId == repositoryId && f.FilePath == filePath);

        if (file == null) return false;

        var symbols = _db.CodeSymbols.Where(s => s.FileId == file.FileId);
        _db.CodeSymbols.RemoveRange(symbols);
        _db.CodeFiles.Remove(file);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task UpsertSymbolsAsync(Guid fileId, Guid repositoryId, List<CodeSymbol> symbols)
    {
        // Remove existing symbols for this file
        var existing = _db.CodeSymbols.Where(s => s.FileId == fileId);
        _db.CodeSymbols.RemoveRange(existing);

        foreach (var symbol in symbols)
        {
            symbol.SymbolId = Guid.NewGuid();
            symbol.FileId = fileId;
            symbol.RepositoryId = repositoryId;
            symbol.IndexedAt = DateTimeOffset.UtcNow;
        }

        _db.CodeSymbols.AddRange(symbols);
        await _db.SaveChangesAsync();
    }

    public async Task<List<CodeFile>> GetFileTreeAsync(Guid repositoryId)
    {
        return await _db.CodeFiles
            .Where(f => f.RepositoryId == repositoryId)
            .OrderBy(f => f.FilePath)
            .ToListAsync();
    }

    public async Task<List<CodeSymbol>> GetFileOutlineAsync(Guid repositoryId, string filePath)
    {
        var file = await _db.CodeFiles
            .FirstOrDefaultAsync(f => f.RepositoryId == repositoryId && f.FilePath == filePath);

        if (file == null) return [];

        return await _db.CodeSymbols
            .Where(s => s.FileId == file.FileId)
            .OrderBy(s => s.StartLine)
            .ToListAsync();
    }

    public async Task<CodeSymbol?> GetSymbolByKeyAsync(Guid repositoryId, string symbolKey)
    {
        return await _db.CodeSymbols
            .FirstOrDefaultAsync(s => s.RepositoryId == repositoryId && s.SymbolKey == symbolKey);
    }

    public async Task<List<CodeSymbol>> GetSymbolsByKeysAsync(Guid repositoryId, List<string> symbolKeys)
    {
        return await _db.CodeSymbols
            .Where(s => s.RepositoryId == repositoryId && symbolKeys.Contains(s.SymbolKey))
            .ToListAsync();
    }

    public async Task<List<CodeSymbol>> SearchSymbolsAsync(string query, Guid? repositoryId, string? kind, int limit)
    {
        var q = _db.CodeSymbols.AsQueryable();

        if (repositoryId.HasValue)
            q = q.Where(s => s.RepositoryId == repositoryId.Value);

        if (!string.IsNullOrEmpty(kind))
            q = q.Where(s => s.Kind == kind);

        // Case-insensitive LIKE search on name and qualified name
        q = q.Where(s => EF.Functions.Like(s.Name, $"%{query}%")
                      || EF.Functions.Like(s.QualifiedName, $"%{query}%"));

        return await q.OrderBy(s => s.Name).Take(limit).ToListAsync();
    }

    public async Task UpdateRepositoryStatsAsync(Guid repositoryId)
    {
        var repo = await _db.CodeRepositories.FindAsync(repositoryId);
        if (repo == null) return;

        repo.FileCount = await _db.CodeFiles.CountAsync(f => f.RepositoryId == repositoryId);
        repo.SymbolCount = await _db.CodeSymbols.CountAsync(s => s.RepositoryId == repositoryId);
        repo.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
    }
}
