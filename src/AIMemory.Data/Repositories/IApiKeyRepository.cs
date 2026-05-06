using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface IApiKeyRepository
{
    Task<ApiKey> CreateAsync(ApiKey key);
    Task<ApiKey?> GetByHashAsync(string keyHash);
    Task<List<ApiKey>> ListAsync();
    Task<ApiKey?> GetByIdAsync(Guid id);
    Task UpdateLastUsedAsync(Guid id);
    Task RevokeAsync(Guid id);
    Task DeleteAsync(Guid id);
    Task<ApiKey?> UpdateAsync(Guid id, Action<ApiKey> update);
}
