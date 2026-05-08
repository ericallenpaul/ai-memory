using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Primary's record of paired secondary hosts. Phase 6 establishes the entity + repo so
/// that phase 7a can wire in the <c>POST /api/pairings</c> endpoints without further DB work.
/// </summary>
public interface IPairingRepository
{
    Task<Pairing> CreateAsync(Pairing pairing);
    Task<Pairing?> GetAsync(Guid pairingId);
    Task<List<Pairing>> ListAsync(bool includeRevoked);
    Task<Pairing?> GetActiveByHostIdAsync(string hostId);
    Task<bool> RevokeAsync(Guid pairingId);
    Task UpdateLastContactAsync(Guid pairingId, DateTimeOffset when);
}
