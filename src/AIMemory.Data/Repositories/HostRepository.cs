using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

public class HostRepository : IHostRepository
{
    private readonly AIMemoryDbContext _db;

    public HostRepository(AIMemoryDbContext db) { _db = db; }

    public async Task<Host> UpsertAsync(Host host)
    {
        if (string.IsNullOrEmpty(host.HostId))
            throw new ArgumentException("HostId must be set", nameof(host));

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Hosts.FirstOrDefaultAsync(h => h.HostId == host.HostId);
        if (existing != null)
        {
            existing.LastSeenAt = now;
            // Don't silently rename — but keep IsLocal source-of-truth on this primary.
            existing.IsLocal = host.IsLocal;
            await _db.SaveChangesAsync();
            return existing;
        }

        host.FirstSeenAt = now;
        host.LastSeenAt = now;
        host.FriendlyName = await DeColisionFriendlyNameAsync(host.FriendlyName);
        _db.Hosts.Add(host);
        await _db.SaveChangesAsync();
        return host;
    }

    public Task<Host?> GetAsync(string hostId)
        => _db.Hosts.FirstOrDefaultAsync(h => h.HostId == hostId);

    public Task<Host?> GetLocalAsync()
        => _db.Hosts.FirstOrDefaultAsync(h => h.IsLocal);

    public Task<List<Host>> ListAsync()
        => _db.Hosts.OrderByDescending(h => h.LastSeenAt).ToListAsync();

    private async Task<string> DeColisionFriendlyNameAsync(string desired)
    {
        if (string.IsNullOrWhiteSpace(desired))
            desired = "host";

        var candidate = desired;
        int suffix = 2;
        while (await _db.Hosts.AnyAsync(h => h.FriendlyName == candidate))
        {
            candidate = $"{desired}-{suffix}";
            suffix++;
            if (suffix > 99) // sanity: don't loop forever on pathological data
                throw new InvalidOperationException(
                    $"Cannot find unique friendly name for '{desired}' after 99 attempts");
        }
        return candidate;
    }
}
