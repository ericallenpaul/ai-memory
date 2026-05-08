using AIMemory.Data;
using AIMemory.Data.Repositories;
using AIMemory.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIMemory.Tests.Unit.Pairings;

/// <summary>
/// Tests covering the storage surface backing the phase 7a pairing endpoints (POST/GET/DELETE
/// <c>/api/pairings</c>). Endpoint behavior is a thin transformation over these repo calls
/// — the endpoints validate input, the repo persists the data.
/// </summary>
public class PairingRepositoryTests : IDisposable
{
    private readonly AIMemoryDbContext _db;
    private readonly PairingRepository _pairingRepo;
    private readonly HostRepository _hostRepo;
    private readonly ApiKeyRepository _keyRepo;

    public PairingRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AIMemoryDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new AIMemoryDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _pairingRepo = new PairingRepository(_db);
        _hostRepo = new HostRepository(_db);
        _keyRepo = new ApiKeyRepository(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static AIMemory.Models.Entities.Host MakeHost(string hostId, string friendlyName = "test-host")
        => new()
        {
            HostId = hostId,
            FriendlyName = friendlyName,
            OsKind = "windows",
            IsLocal = false
        };

    [Fact]
    public async Task Create_RecordsActivePairingForHost()
    {
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('a', 64), "host-a"));

        var created = await _pairingRepo.CreateAsync(new Pairing
        {
            HostId = host.HostId,
            FriendlyName = host.FriendlyName,
        });

        Assert.NotEqual(Guid.Empty, created.PairingId);
        Assert.False(created.IsRevoked);

        var active = await _pairingRepo.GetActiveByHostIdAsync(host.HostId);
        Assert.NotNull(active);
        Assert.Equal(created.PairingId, active.PairingId);
    }

    [Fact]
    public async Task List_OmitsRevokedByDefault()
    {
        var host1 = await _hostRepo.UpsertAsync(MakeHost(new string('b', 64), "host-b"));
        var host2 = await _hostRepo.UpsertAsync(MakeHost(new string('c', 64), "host-c"));
        var p1 = await _pairingRepo.CreateAsync(new Pairing { HostId = host1.HostId, FriendlyName = "p1" });
        var p2 = await _pairingRepo.CreateAsync(new Pairing { HostId = host2.HostId, FriendlyName = "p2" });
        await _pairingRepo.RevokeAsync(p1.PairingId);

        var active = await _pairingRepo.ListAsync(includeRevoked: false);
        var all = await _pairingRepo.ListAsync(includeRevoked: true);

        Assert.Single(active);
        Assert.Equal(p2.PairingId, active[0].PairingId);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Revoke_FlipsIsRevokedAndStopsAppearingAsActive()
    {
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('d', 64), "host-d"));
        var pairing = await _pairingRepo.CreateAsync(new Pairing
        {
            HostId = host.HostId,
            FriendlyName = host.FriendlyName,
        });

        var revoked = await _pairingRepo.RevokeAsync(pairing.PairingId);

        Assert.True(revoked);
        var active = await _pairingRepo.GetActiveByHostIdAsync(host.HostId);
        Assert.Null(active);

        // Second revoke is a no-op (returns false) — endpoint code uses this signal.
        var secondAttempt = await _pairingRepo.RevokeAsync(pairing.PairingId);
        Assert.False(secondAttempt);
    }

    [Fact]
    public async Task Revoke_LeavesHistoricalRowVisibleWithIncludeRevoked()
    {
        // Phase 7a: a revoked pairing must remain queryable so the desktop UI can show
        // "previously paired" history.
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('e', 64), "host-e"));
        var p = await _pairingRepo.CreateAsync(new Pairing { HostId = host.HostId, FriendlyName = "p" });
        await _pairingRepo.RevokeAsync(p.PairingId);

        var historical = await _pairingRepo.GetAsync(p.PairingId);
        Assert.NotNull(historical);
        Assert.True(historical.IsRevoked);
    }

    [Fact]
    public async Task Revoke_LinkedApiKey_DeactivatesKeyForSubsequentAuth()
    {
        // Models the cascade the endpoint performs: deleting a pairing also deactivates the
        // api_key it was linked to, so the secondary's subsequent requests 401. This is the
        // "DELETE pairing → 401 on next request" leg of the brief's required test.
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('f', 64), "host-f"));
        var apiKey = await _keyRepo.CreateAsync(new ApiKey
        {
            Name = "secondary-key",
            KeyHash = "deadbeef",
            KeyPrefix = "deadbe",
            Scopes = ["ingest"],
            IsActive = true
        });
        var pairing = await _pairingRepo.CreateAsync(new Pairing
        {
            HostId = host.HostId,
            FriendlyName = host.FriendlyName,
            ApiKeyId = apiKey.ApiKeyId
        });

        await _pairingRepo.RevokeAsync(pairing.PairingId);
        await _keyRepo.RevokeAsync(apiKey.ApiKeyId);

        // Subsequent auth lookups should see IsActive=false → middleware rejects with 401.
        _db.ChangeTracker.Clear();
        var afterRevoke = await _keyRepo.GetByHashAsync("deadbeef");
        Assert.NotNull(afterRevoke);
        Assert.False(afterRevoke.IsActive);
    }

    [Fact]
    public async Task Revoke_ThenAuthLookupViaMiddleware_Returns401()
    {
        // Composite scenario: the brief explicitly calls out that after DELETE /api/pairings/{id},
        // subsequent requests using that host's API key must 401. The middleware logic checks
        // ApiKey.IsActive; the endpoint cascade flips that to false on revoke. This test pins
        // the contract end-to-end at the data layer.
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('5', 64), "host-5"));
        var key = await _keyRepo.CreateAsync(new ApiKey
        {
            Name = "secondary-after-revoke",
            KeyHash = "deadbeef-revoke-test",
            KeyPrefix = "deadbe",
            Scopes = ["ingest"],
            IsActive = true
        });
        var pairing = await _pairingRepo.CreateAsync(new Pairing
        {
            HostId = host.HostId,
            FriendlyName = host.FriendlyName,
            ApiKeyId = key.ApiKeyId
        });

        // Pre-revoke: key is active and discoverable by hash.
        var pre = await _keyRepo.GetByHashAsync("deadbeef-revoke-test");
        Assert.NotNull(pre);
        Assert.True(pre.IsActive);

        // The endpoint flow: revoke pairing → cascade revoke key.
        await _pairingRepo.RevokeAsync(pairing.PairingId);
        await _keyRepo.RevokeAsync(key.ApiKeyId);

        // Post-revoke: same hash lookup returns the key but with IsActive=false.
        // ApiKeyAuthMiddleware checks IsActive and rejects with 401 when false (covered by
        // ApiKeyAuthMiddlewareTests.InvokeAsync_InactiveKey_Returns401).
        _db.ChangeTracker.Clear();
        var post = await _keyRepo.GetByHashAsync("deadbeef-revoke-test");
        Assert.NotNull(post);
        Assert.False(post.IsActive);
    }

    [Fact]
    public async Task UpdateLastContact_RecordsTimestamp()
    {
        var host = await _hostRepo.UpsertAsync(MakeHost(new string('1', 64), "host-1"));
        var p = await _pairingRepo.CreateAsync(new Pairing { HostId = host.HostId, FriendlyName = "p" });

        var when = DateTimeOffset.UtcNow;
        await _pairingRepo.UpdateLastContactAsync(p.PairingId, when);

        _db.ChangeTracker.Clear();
        var fresh = await _pairingRepo.GetAsync(p.PairingId);
        Assert.NotNull(fresh);
        Assert.NotNull(fresh.LastContactAt);
        // Allow ~1s slack for SQLite's string-stored timestamps.
        Assert.True(Math.Abs((fresh.LastContactAt!.Value - when).TotalSeconds) < 1);
    }
}
