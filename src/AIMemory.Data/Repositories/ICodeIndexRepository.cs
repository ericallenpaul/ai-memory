using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public interface ICodeIndexRepository
{
    Task<CodeRepository> UpsertRepositoryAsync(CodeRepository repo);
    Task<CodeRepository?> GetRepositoryAsync(Guid id);
    Task<CodeRepository?> GetRepositoryByNameAsync(string name);
    Task<List<CodeRepository>> ListRepositoriesAsync();
    Task DeleteRepositoryAsync(Guid id);
    Task UpsertFileAsync(CodeFile file);
    Task DeleteStaleFilesAsync(Guid repositoryId, IEnumerable<string> currentFilePaths);
    Task<bool> DeleteFileAsync(Guid repositoryId, string filePath);
    Task UpsertSymbolsAsync(Guid fileId, Guid repositoryId, List<CodeSymbol> symbols);
    Task<List<CodeFile>> GetFileTreeAsync(Guid repositoryId);
    Task<List<CodeSymbol>> GetFileOutlineAsync(Guid repositoryId, string filePath);
    Task<CodeSymbol?> GetSymbolByKeyAsync(Guid repositoryId, string symbolKey);
    Task<List<CodeSymbol>> GetSymbolsByKeysAsync(Guid repositoryId, List<string> symbolKeys);
    Task<List<CodeSymbol>> SearchSymbolsAsync(string query, Guid? repositoryId, string? kind, int limit);
    Task UpdateRepositoryStatsAsync(Guid repositoryId);
}
