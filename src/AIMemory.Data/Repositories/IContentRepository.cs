using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Read-only access to content blobs (<see cref="CodeFile"/> rows).
/// Writes happen through <see cref="ICodeIndexRepository.UpsertFileAsync"/> which co-manages
/// the content blob and the per-host file_location atomically.
/// </summary>
public interface IContentRepository
{
    Task<CodeFile?> GetAsync(string contentSha256);
    Task<List<CodeFile>> GetByHashesAsync(IEnumerable<string> contentSha256s);
    Task<int> CountAsync();
}
