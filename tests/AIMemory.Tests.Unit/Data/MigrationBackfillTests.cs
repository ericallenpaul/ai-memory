using AIMemory.Data;
using AIMemory.Data.Migrations;
using AIMemory.Data.Repositories;
using AIMemory.Identity;
using AIMemory.Models.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Unit.Data;

/// <summary>
/// Verifies the AddDistributedIdentity migration shape: applying it to a fresh in-memory
/// SQLite database produces the expected new tables, the local host row, and a working
/// <c>code_repositories</c> compatibility view over <c>projects</c>.
/// </summary>
public class MigrationBackfillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<AIMemoryDbContext> _opts;

    public MigrationBackfillTests()
    {
        // We use a file-backed db (not :memory:) because EF migrations on a single shared
        // connection are flaky across test contexts; a temp file gives us a clean slate.
        _dbPath = Path.Combine(Path.GetTempPath(), $"aimemory-mig-test-{Guid.NewGuid():N}.db");
        _opts = new DbContextOptionsBuilder<AIMemoryDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Migration_AppliesCleanly_AndCreatesNewTables()
    {
        await using var db = new AIMemoryDbContext(_opts);
        await db.Database.MigrateAsync();

        // New tables exist.
        Assert.True(await TableExistsAsync(db, "hosts"));
        Assert.True(await TableExistsAsync(db, "projects"));
        Assert.True(await TableExistsAsync(db, "file_locations"));
        Assert.True(await TableExistsAsync(db, "pairings"));

        // Legacy code_repositories table is gone, but the VIEW takes its place.
        Assert.Equal("view", await ObjectTypeAsync(db, "code_repositories"));

        // code_files reshape: now keyed by content_sha256.
        Assert.True(await ColumnExistsAsync(db, "code_files", "content_sha256"));
        Assert.False(await ColumnExistsAsync(db, "code_files", "file_id"));

        // code_symbols reshape: project_id + content_sha256 columns present.
        Assert.True(await ColumnExistsAsync(db, "code_symbols", "project_id"));
        Assert.True(await ColumnExistsAsync(db, "code_symbols", "content_sha256"));
    }

    [Fact]
    public async Task Migration_InsertsLocalHostRow()
    {
        await using var db = new AIMemoryDbContext(_opts);
        await db.Database.MigrateAsync();

        var local = await db.Hosts.FirstOrDefaultAsync(h => h.IsLocal);
        Assert.NotNull(local);
        Assert.Equal(64, local!.HostId.Length); // sha-256 hex
        Assert.NotEmpty(local.FriendlyName);
        Assert.Contains(local.OsKind, new[] { "windows", "linux", "macos", "unknown" });
    }

    [Fact]
    public async Task CodeRepositoriesView_QueryableAfterMigration()
    {
        await using var db = new AIMemoryDbContext(_opts);
        await db.Database.MigrateAsync();

        // The view should exist and return zero rows on a fresh database (no projects yet).
        var rowCount = await ScalarLongAsync(db, "SELECT COUNT(*) FROM code_repositories;");
        Assert.Equal(0, rowCount);

        // Insert a project row directly; the view should reflect it.
        db.Projects.Add(new Project
        {
            ProjectId = new string('a', 64),
            DisplayName = "test-proj",
            IdentityKind = "fallback",
            SourceType = "local",
            SourcePath = "C:\\fake",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        rowCount = await ScalarLongAsync(db, "SELECT COUNT(*) FROM code_repositories;");
        Assert.Equal(1, rowCount);

        // Column shape matches legacy: repository_id, name, source_type, etc.
        var name = await ScalarStringAsync(db, "SELECT name FROM code_repositories LIMIT 1;");
        Assert.Equal("test-proj", name);
    }

    [Fact]
    public async Task LegacyProjectMigrator_NoOpsWhenNoFallbackProjects()
    {
        await using var db = new AIMemoryDbContext(_opts);
        await db.Database.MigrateAsync();

        var saltDir = Path.Combine(Path.GetTempPath(), $"salt-{Guid.NewGuid():N}");
        try
        {
            var saltStore = new InstallSaltStore(saltDir);
            var hostIds = new HostIdProvider(saltStore);
            var resolver = new ProjectIdResolver();
            var migrator = new LegacyProjectMigrator(db, resolver, hostIds, NullLogger<LegacyProjectMigrator>.Instance);

            // Should not throw — tolerates an empty project set.
            await migrator.RunIfPendingAsync();
        }
        finally
        {
            if (Directory.Exists(saltDir)) Directory.Delete(saltDir, true);
        }
    }

    // ---- helpers ----

    private static async Task<bool> TableExistsAsync(AIMemoryDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
            var p = cmd.CreateParameter(); p.ParameterName = "@n"; p.Value = name; cmd.Parameters.Add(p);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<string> ObjectTypeAsync(AIMemoryDbContext db, string name)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type FROM sqlite_master WHERE name=@n;";
            var p = cmd.CreateParameter(); p.ParameterName = "@n"; p.Value = name; cmd.Parameters.Add(p);
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<bool> ColumnExistsAsync(AIMemoryDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<long> ScalarLongAsync(AIMemoryDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<string> ScalarStringAsync(AIMemoryDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        }
        finally { await conn.CloseAsync(); }
    }
}
