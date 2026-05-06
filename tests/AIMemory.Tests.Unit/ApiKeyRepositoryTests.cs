using AIMemory.Data;
using AIMemory.Data.Repositories;
using AIMemory.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIMemory.Tests.Unit;

/// <summary>
/// Tests for ApiKeyRepository using an in-memory SQLite database.
/// Each test gets its own isolated database instance.
/// </summary>
public class ApiKeyRepositoryTests : IDisposable
{
    private readonly AIMemoryDbContext _db;
    private readonly ApiKeyRepository _repo;

    public ApiKeyRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AIMemoryDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new AIMemoryDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _repo = new ApiKeyRepository(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static ApiKey MakeKey(string name = "test-key", string hash = "abc123", string prefix = "ak_test")
    {
        return new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            Name = name,
            KeyHash = hash,
            KeyPrefix = prefix,
            Scopes = ["ingest"],
            IsActive = true,
        };
    }

    // CreateAsync tests

    [Fact]
    public async Task CreateAsync_StoresKeyInDatabase()
    {
        var key = MakeKey("my-key", "hash001", "ak_001");

        var created = await _repo.CreateAsync(key);

        var found = await _db.ApiKeys.FindAsync(created.ApiKeyId);
        Assert.NotNull(found);
        Assert.Equal("my-key", found.Name);
    }

    [Fact]
    public async Task CreateAsync_SetsCreatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var key = MakeKey("key-with-time", "hash002", "ak_002");

        var created = await _repo.CreateAsync(key);

        Assert.True(created.CreatedAt >= before,
            $"CreatedAt {created.CreatedAt} should be >= {before}");
    }

    [Fact]
    public async Task CreateAsync_ReturnsCreatedKey()
    {
        var key = MakeKey("returned-key", "hash003", "ak_003");

        var result = await _repo.CreateAsync(key);

        Assert.NotNull(result);
        Assert.Equal(key.ApiKeyId, result.ApiKeyId);
    }

    // GetByHashAsync tests

    [Fact]
    public async Task GetByHashAsync_FindsExistingKeyByHash()
    {
        var key = MakeKey("lookup-key", "uniquehash999", "ak_lookup");
        await _repo.CreateAsync(key);

        var found = await _repo.GetByHashAsync("uniquehash999");

        Assert.NotNull(found);
        Assert.Equal("lookup-key", found.Name);
    }

    [Fact]
    public async Task GetByHashAsync_ReturnsNullForUnknownHash()
    {
        var found = await _repo.GetByHashAsync("does-not-exist");

        Assert.Null(found);
    }

    [Fact]
    public async Task GetByHashAsync_ReturnsCorrectKeyWhenMultipleExist()
    {
        await _repo.CreateAsync(MakeKey("key-a", "hash-a-111", "ak_a"));
        await _repo.CreateAsync(MakeKey("key-b", "hash-b-222", "ak_b"));

        var found = await _repo.GetByHashAsync("hash-b-222");

        Assert.NotNull(found);
        Assert.Equal("key-b", found.Name);
    }

    // RevokeAsync tests

    [Fact]
    public async Task RevokeAsync_SetsIsActiveFalse()
    {
        var key = MakeKey("active-key", "hash-revoke-1", "ak_rev1");
        var created = await _repo.CreateAsync(key);
        Assert.True(created.IsActive);

        await _repo.RevokeAsync(created.ApiKeyId);

        // Re-query to see updated state
        _db.ChangeTracker.Clear();
        var updated = await _db.ApiKeys.FindAsync(created.ApiKeyId);
        Assert.NotNull(updated);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task RevokeAsync_DoesNotThrowForUnknownId()
    {
        // Should silently do nothing (ExecuteUpdateAsync with no matches)
        var exception = await Record.ExceptionAsync(() => _repo.RevokeAsync(Guid.NewGuid()));

        Assert.Null(exception);
    }

    // DeleteAsync tests

    [Fact]
    public async Task DeleteAsync_RemovesKeyFromDatabase()
    {
        var key = MakeKey("delete-me", "hash-del-1", "ak_del1");
        var created = await _repo.CreateAsync(key);

        await _repo.DeleteAsync(created.ApiKeyId);

        _db.ChangeTracker.Clear();
        var found = await _db.ApiKeys.FindAsync(created.ApiKeyId);
        Assert.Null(found);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotAffectOtherKeys()
    {
        var keyA = await _repo.CreateAsync(MakeKey("keep-a", "hash-keep-a", "ak_ka"));
        var keyB = await _repo.CreateAsync(MakeKey("delete-b", "hash-del-b", "ak_kb"));

        await _repo.DeleteAsync(keyB.ApiKeyId);

        _db.ChangeTracker.Clear();
        var stillThere = await _db.ApiKeys.FindAsync(keyA.ApiKeyId);
        Assert.NotNull(stillThere);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrowForUnknownId()
    {
        var exception = await Record.ExceptionAsync(() => _repo.DeleteAsync(Guid.NewGuid()));

        Assert.Null(exception);
    }

    // ListAsync tests

    [Fact]
    public async Task ListAsync_ReturnsAllKeys()
    {
        await _repo.CreateAsync(MakeKey("list-1", "hash-list-1", "ak_l1"));
        await _repo.CreateAsync(MakeKey("list-2", "hash-list-2", "ak_l2"));
        await _repo.CreateAsync(MakeKey("list-3", "hash-list-3", "ak_l3"));

        var keys = await _repo.ListAsync();

        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyListWhenNoKeys()
    {
        var keys = await _repo.ListAsync();

        Assert.Empty(keys);
    }

    // GetByIdAsync tests

    [Fact]
    public async Task GetByIdAsync_FindsExistingKey()
    {
        var key = MakeKey("by-id", "hash-byid", "ak_byid");
        var created = await _repo.CreateAsync(key);

        var found = await _repo.GetByIdAsync(created.ApiKeyId);

        Assert.NotNull(found);
        Assert.Equal("by-id", found.Name);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullForUnknownId()
    {
        var found = await _repo.GetByIdAsync(Guid.NewGuid());

        Assert.Null(found);
    }

    // UpdateAsync tests

    [Fact]
    public async Task UpdateAsync_AppliesUpdateAction()
    {
        var key = MakeKey("update-me", "hash-upd", "ak_upd");
        var created = await _repo.CreateAsync(key);

        await _repo.UpdateAsync(created.ApiKeyId, k => k.Name = "updated-name");

        _db.ChangeTracker.Clear();
        var updated = await _db.ApiKeys.FindAsync(created.ApiKeyId);
        Assert.NotNull(updated);
        Assert.Equal("updated-name", updated.Name);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsNullForUnknownId()
    {
        var result = await _repo.UpdateAsync(Guid.NewGuid(), k => k.Name = "irrelevant");

        Assert.Null(result);
    }
}
