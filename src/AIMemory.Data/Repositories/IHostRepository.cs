using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Persistence for <see cref="Host"/> rows — the set of machines that have ever ingested
/// into this primary.
/// </summary>
public interface IHostRepository
{
    /// <summary>
    /// Inserts a row if absent; updates <c>LastSeenAt</c> if present. Returns the persisted entity.
    /// Resolves <c>friendly_name</c> collisions by appending <c>-2</c>, <c>-3</c>, ...
    /// </summary>
    Task<Host> UpsertAsync(Host host);

    Task<Host?> GetAsync(string hostId);

    Task<Host?> GetLocalAsync();

    Task<List<Host>> ListAsync();
}
