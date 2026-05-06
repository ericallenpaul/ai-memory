using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private readonly AIMemoryDbContext _db;

    public ApiKeyRepository(AIMemoryDbContext db)
    {
        _db = db;
    }

    public async Task<ApiKey> CreateAsync(ApiKey key)
    {
        key.CreatedAt = DateTimeOffset.UtcNow;
        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync();
        return key;
    }

    public async Task<ApiKey?> GetByHashAsync(string keyHash)
    {
        return await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == keyHash);
    }

    public async Task<List<ApiKey>> ListAsync()
    {
        return await _db.ApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    public async Task<ApiKey?> GetByIdAsync(Guid id)
    {
        return await _db.ApiKeys.FindAsync(id);
    }

    public async Task UpdateLastUsedAsync(Guid id)
    {
        await _db.ApiKeys
            .Where(k => k.ApiKeyId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow));
    }

    public async Task RevokeAsync(Guid id)
    {
        await _db.ApiKeys
            .Where(k => k.ApiKeyId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsActive, false));
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.ApiKeys.Where(k => k.ApiKeyId == id).ExecuteDeleteAsync();
    }

    public async Task<ApiKey?> UpdateAsync(Guid id, Action<ApiKey> update)
    {
        var key = await _db.ApiKeys.FindAsync(id);
        if (key == null) return null;

        update(key);
        await _db.SaveChangesAsync();
        return key;
    }
}
