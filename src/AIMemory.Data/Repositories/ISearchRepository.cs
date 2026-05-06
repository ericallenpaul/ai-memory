using AIMemory.Models.Dtos;

namespace AIMemory.Data.Repositories;

public interface ISearchRepository
{
    Task<List<SearchResult>> SearchAsync(string query, string? project = null, string? repo = null,
        string? source = null, int limit = 10, int offset = 0);
}
