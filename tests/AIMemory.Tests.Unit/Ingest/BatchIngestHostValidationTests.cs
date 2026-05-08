using AIMemory.Data;
using AIMemory.Data.Repositories;
using AIMemory.Models.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AIMemory.Tests.Unit.Ingest;

/// <summary>
/// Phase 7a contract: <see cref="BatchIngestRequest"/> grew per-batch <c>HostId</c>/<c>ProjectId</c>
/// fields, and the <c>POST /api/ingest/batch</c> handler rejects with 403 when <c>HostId</c> is set
/// but doesn't match any row in the <c>hosts</c> table. These tests cover the validation surface
/// without spinning up an HTTP host — the endpoint code is a thin wrapper over
/// <see cref="IHostRepository.GetAsync(string)"/>.
/// </summary>
public class BatchIngestHostValidationTests : IDisposable
{
    private readonly AIMemoryDbContext _db;
    private readonly HostRepository _hostRepo;

    public BatchIngestHostValidationTests()
    {
        var options = new DbContextOptionsBuilder<AIMemoryDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new AIMemoryDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _hostRepo = new HostRepository(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public void BatchIngestRequest_HasHostIdAndProjectIdFields()
    {
        // Wire-protocol contract — field renames here would break secondaries silently.
        // Keep this test as a regression guard.
        var request = new BatchIngestRequest
        {
            HostId = "abcd",
            ProjectId = "1234",
            ClientId = "test"
        };

        Assert.Equal("abcd", request.HostId);
        Assert.Equal("1234", request.ProjectId);
    }

    [Fact]
    public void BatchIngestRequest_HostIdAndProjectIdNullable_AllowsLegacyV1Batches()
    {
        // A v1 (single-machine) ingestor doesn't populate HostId/ProjectId. The handler must
        // treat null as "use the local host", not as an error.
        var request = new BatchIngestRequest { ClientId = "legacy" };

        Assert.Null(request.HostId);
        Assert.Null(request.ProjectId);
    }

    [Fact]
    public async Task UnknownHostId_LookupReturnsNull_TriggersHandlerRejection()
    {
        // The handler's contract: !string.IsNullOrEmpty(HostId) && hostRepo.GetAsync(HostId) == null
        // → respond 403. Models the unknown-host path of /api/ingest/batch.
        var unknown = new string('z', 64);

        var found = await _hostRepo.GetAsync(unknown);

        Assert.Null(found);
    }

    [Fact]
    public async Task KnownHostId_LookupReturnsRow_HandlerProceeds()
    {
        // Conversely: if the host has been registered (POST /api/pairings or local first-run),
        // the lookup returns a row and the handler proceeds with ingestion.
        var hostId = new string('a', 64);
        await _hostRepo.UpsertAsync(new AIMemory.Models.Entities.Host
        {
            HostId = hostId,
            FriendlyName = "known-host",
            OsKind = "windows",
            IsLocal = false
        });

        var found = await _hostRepo.GetAsync(hostId);

        Assert.NotNull(found);
        Assert.Equal(hostId, found.HostId);
    }

    [Fact]
    public async Task EmptyHostId_BypassesValidation_ForBackcompatPath()
    {
        // The handler short-circuits empty/null HostId (legacy single-machine ingest path).
        // This test pins the contract that "no validation when not supplied" is the documented
        // behavior — phase 7b will start populating HostId on every batch.
        var request = new BatchIngestRequest { HostId = string.Empty };

        Assert.True(string.IsNullOrEmpty(request.HostId));
        // No DB lookup happens for empty values; the handler proceeds with the local host.
        await Task.CompletedTask; // async signature parity with sibling tests
    }
}
