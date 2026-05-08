using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class PairingRepository : IPairingRepository
{
    private readonly AIMemoryDbContext _db;
    public PairingRepository(AIMemoryDbContext db) { _db = db; }

    public async Task<Pairing> CreateAsync(Pairing pairing)
    {
        if (pairing.PairingId == Guid.Empty)
            pairing.PairingId = Guid.NewGuid();
        if (pairing.PairedAt == default)
            pairing.PairedAt = DateTimeOffset.UtcNow;

        _db.Pairings.Add(pairing);
        await _db.SaveChangesAsync();
        return pairing;
    }

    public Task<Pairing?> GetAsync(Guid pairingId)
        => _db.Pairings.FirstOrDefaultAsync(p => p.PairingId == pairingId);

    public Task<List<Pairing>> ListAsync(bool includeRevoked)
    {
        var q = _db.Pairings.AsQueryable();
        if (!includeRevoked) q = q.Where(p => !p.IsRevoked);
        return q.OrderByDescending(p => p.PairedAt).ToListAsync();
    }

    public Task<Pairing?> GetActiveByHostIdAsync(string hostId)
        => _db.Pairings.FirstOrDefaultAsync(p => p.HostId == hostId && !p.IsRevoked);

    public async Task<bool> RevokeAsync(Guid pairingId)
    {
        var p = await _db.Pairings.FirstOrDefaultAsync(x => x.PairingId == pairingId);
        if (p == null || p.IsRevoked) return false;
        p.IsRevoked = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task UpdateLastContactAsync(Guid pairingId, DateTimeOffset when)
    {
        var p = await _db.Pairings.FirstOrDefaultAsync(x => x.PairingId == pairingId);
        if (p == null) return;
        p.LastContactAt = when;
        await _db.SaveChangesAsync();
    }
}
