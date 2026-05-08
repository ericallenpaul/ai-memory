using AIMemory.Data;
using AIMemory.Data.Migrations;
using AIMemory.Identity;
using AIMemory.Models.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Tests.Unit.Data;

/// <summary>
/// Verifies the AddDistributedIdentity migration's backfill correctness when applied to a
/// pre-phase-6 database. We can't easily replay the OLD migrations against the NEW model
/// (the old entity types are gone), so we hand-build a v0 schema with seed rows, then
/// stamp the EF history table to make EF think only the prior migrations have run, then
/// invoke <c>Database.Migrate()</c> to apply just <c>AddDistributedIdentity</c>.
/// </summary>
public class MigrationBackfillSeededDataTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationBackfillSeededDataTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"aimemory-mig-seed-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private DbContextOptions<AIMemoryDbContext> Opts() =>
        new DbContextOptionsBuilder<AIMemoryDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

    [Fact]
    public async Task Backfill_ProducesProjectAndFileLocationFromLegacyRepo()
    {
        SeedLegacySchemaWithData(out var repoId, out var fileContentHash, out _);

        await using var db = new AIMemoryDbContext(Opts());
        await db.Database.MigrateAsync();

        // The legacy repo became a Project row.
        var projects = await db.Projects.ToListAsync();
        Assert.Single(projects);
        var project = projects[0];
        Assert.Equal("legacy-test-repo", project.DisplayName);
        Assert.NotEqual(repoId.ToString(), project.ProjectId); // synthetic id, not the GUID
        Assert.Equal(64, project.ProjectId.Length);

        // The legacy file became a content-addressed code_file + per-host file_location.
        var contents = await db.CodeFiles.ToListAsync();
        Assert.Single(contents);
        Assert.Equal(fileContentHash, contents[0].ContentSha256);

        var locations = await db.FileLocations.ToListAsync();
        Assert.Single(locations);
        Assert.Equal("src/Foo.cs", locations[0].RelPath);
        Assert.Equal(fileContentHash, locations[0].ContentSha256);
        Assert.Equal(project.ProjectId, locations[0].ProjectId);

        // Local host has been created.
        var local = await db.Hosts.FirstAsync(h => h.IsLocal);
        Assert.Equal(local.HostId, locations[0].HostId);
    }

    [Fact]
    public async Task Backfill_RekeysSymbolsToProjectIdScopedKey()
    {
        SeedLegacySchemaWithData(out _, out _, out _);

        await using var db = new AIMemoryDbContext(Opts());
        await db.Database.MigrateAsync();

        var symbols = await db.CodeSymbols.ToListAsync();
        Assert.Single(symbols);
        var symbol = symbols[0];
        var project = await db.Projects.FirstAsync();

        // New symbol_key format: project_id::QualifiedName#kind.
        Assert.StartsWith($"{project.ProjectId}::", symbol.SymbolKey);
        Assert.EndsWith("#class", symbol.SymbolKey);
        Assert.Contains("MyApp.Foo", symbol.SymbolKey);
        Assert.Equal(project.ProjectId, symbol.ProjectId);
    }

    [Fact]
    public async Task Backfill_PreservesParentSymbolKey_RewrittenToProjectIdPrefix()
    {
        SeedLegacySchemaWithData(out _, out _, out _);

        // Add a child symbol with parent_symbol_key set to the legacy filepath::Q#k format.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO code_symbols (symbol_id, file_id, repository_id, symbol_key, name, qualified_name,
                          kind, signature, summary, start_line, end_line, start_byte, end_byte,
                          parent_symbol_key, indexed_at)
VALUES (@sid, @fid, @rid, @sk, 'Bar', 'MyApp.Foo.Bar', 'method', NULL, NULL, 5, 10, 50, 100,
        'src/Foo.cs::MyApp.Foo#class', '2024-01-01T00:00:00Z');";
            cmd.Parameters.Add(new SqliteParameter("@sid", Guid.NewGuid().ToString()));
            // Reuse existing fixed file_id from SeedLegacySchemaWithData.
            cmd.Parameters.Add(new SqliteParameter("@fid", FixedFileId.ToString()));
            cmd.Parameters.Add(new SqliteParameter("@rid", FixedRepoId.ToString()));
            cmd.Parameters.Add(new SqliteParameter("@sk", "src/Foo.cs::MyApp.Foo.Bar#method"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = new AIMemoryDbContext(Opts());
        await db.Database.MigrateAsync();

        var symbols = await db.CodeSymbols.OrderBy(s => s.QualifiedName).ToListAsync();
        Assert.Equal(2, symbols.Count);
        var bar = symbols.First(s => s.Name == "Bar");
        var project = await db.Projects.FirstAsync();
        Assert.Equal($"{project.ProjectId}::MyApp.Foo#class", bar.ParentSymbolKey);
    }

    [Fact]
    public async Task Backfill_LegacyCodeRepositoriesView_ReadsBackInsertedProjects()
    {
        SeedLegacySchemaWithData(out _, out _, out _);

        await using var db = new AIMemoryDbContext(Opts());
        await db.Database.MigrateAsync();

        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, source_path FROM code_repositories;";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("legacy-test-repo", reader.GetString(0));
        Assert.Equal("C:\\fake\\src", reader.GetString(1));
    }

    [Fact]
    public async Task LegacyProjectMigrator_LeavesFallbackInPlace_WhenSourcePathMissing()
    {
        SeedLegacySchemaWithData(out _, out _, out _);

        await using var db = new AIMemoryDbContext(Opts());
        await db.Database.MigrateAsync();

        var saltDir = Path.Combine(Path.GetTempPath(), $"salt-{Guid.NewGuid():N}");
        try
        {
            var saltStore = new InstallSaltStore(saltDir);
            var hostIds = new HostIdProvider(saltStore);
            var resolver = new ProjectIdResolver();
            var migrator = new LegacyProjectMigrator(db, resolver, hostIds, NullLogger<LegacyProjectMigrator>.Instance);

            // Source path C:\\fake\\src doesn't exist → resolver falls back; new fallback id
            // replaces the synthetic GUID-doubled id from the migration.
            await migrator.RunIfPendingAsync();

            var project = await db.Projects.FirstAsync();
            Assert.Equal("fallback", project.IdentityKind);
            Assert.Equal(64, project.ProjectId.Length);
        }
        finally
        {
            if (Directory.Exists(saltDir)) Directory.Delete(saltDir, true);
        }
    }

    // ---- helpers ----

    private static readonly Guid FixedRepoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FixedFileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Builds the database with the v2 schema (everything before AddDistributedIdentity)
    /// and seeds it with one repo + one file + one symbol. Stamps the EF migration history
    /// so EF only runs the new AddDistributedIdentity migration on top.
    /// </summary>
    private void SeedLegacySchemaWithData(out Guid repoId, out string contentHash, out Guid fileId)
    {
        repoId = FixedRepoId;
        fileId = FixedFileId;
        contentHash = new string('a', 64);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // EF migrations history table — required so EF skips the prior migrations and
        // applies only AddDistributedIdentity.
        cmd.CommandText = @"
CREATE TABLE __EFMigrationsHistory (
    MigrationId    TEXT NOT NULL PRIMARY KEY,
    ProductVersion TEXT NOT NULL
);
INSERT INTO __EFMigrationsHistory VALUES ('20260303174723_InitialCreate', '10.0.0');
INSERT INTO __EFMigrationsHistory VALUES ('20260306194735_AddCodeIndex',  '10.0.0');
INSERT INTO __EFMigrationsHistory VALUES ('20260310184641_AddApiKeysAndMachineName', '10.0.0');

-- Pre-phase-6 schema relevant to the migration: code_repositories, code_files, code_symbols.
-- (Other tables aren't touched by the migration so we don't need them for this test.)
CREATE TABLE code_repositories (
    repository_id  TEXT NOT NULL PRIMARY KEY,
    name           TEXT NOT NULL,
    source_type    TEXT NOT NULL,
    source_path    TEXT NOT NULL,
    default_branch TEXT NULL,
    file_count     INTEGER NOT NULL,
    symbol_count   INTEGER NOT NULL,
    indexed_at     TEXT NOT NULL,
    updated_at     TEXT NOT NULL
);

CREATE TABLE code_files (
    file_id          TEXT NOT NULL PRIMARY KEY,
    repository_id    TEXT NOT NULL,
    file_path        TEXT NOT NULL,
    language         TEXT NOT NULL,
    file_size        INTEGER NOT NULL,
    content_hash     TEXT NOT NULL,
    indexed_at       TEXT NOT NULL
);
CREATE INDEX IX_code_files_language ON code_files(language);
CREATE INDEX IX_code_files_repository_id ON code_files(repository_id);
CREATE UNIQUE INDEX IX_code_files_repository_id_file_path ON code_files(repository_id, file_path);

CREATE TABLE code_symbols (
    symbol_id          TEXT NOT NULL PRIMARY KEY,
    file_id            TEXT NOT NULL,
    repository_id      TEXT NOT NULL,
    symbol_key         TEXT NOT NULL,
    name               TEXT NOT NULL,
    qualified_name     TEXT NOT NULL,
    kind               TEXT NOT NULL,
    signature          TEXT NULL,
    summary            TEXT NULL,
    start_line         INTEGER NOT NULL,
    end_line           INTEGER NOT NULL,
    start_byte         INTEGER NOT NULL,
    end_byte           INTEGER NOT NULL,
    parent_symbol_key  TEXT NULL,
    indexed_at         TEXT NOT NULL
);
CREATE INDEX IX_code_symbols_file_id ON code_symbols(file_id);
CREATE INDEX IX_code_symbols_repository_id ON code_symbols(repository_id);
CREATE INDEX IX_code_symbols_symbol_key ON code_symbols(symbol_key);
CREATE INDEX IX_code_symbols_kind ON code_symbols(kind);
CREATE INDEX IX_code_symbols_name ON code_symbols(name);
CREATE INDEX IX_code_symbols_qualified_name ON code_symbols(qualified_name);
";
        cmd.ExecuteNonQuery();

        // Seed: one repo, one file, one symbol.
        cmd.CommandText = @"
INSERT INTO code_repositories (repository_id, name, source_type, source_path, file_count, symbol_count, indexed_at, updated_at)
VALUES (@rid, 'legacy-test-repo', 'local', 'C:\fake\src', 1, 1, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');

INSERT INTO code_files (file_id, repository_id, file_path, language, file_size, content_hash, indexed_at)
VALUES (@fid, @rid, 'src/Foo.cs', 'csharp', 100, @hash, '2024-01-01T00:00:00Z');

INSERT INTO code_symbols (symbol_id, file_id, repository_id, symbol_key, name, qualified_name, kind,
                          signature, summary, start_line, end_line, start_byte, end_byte,
                          parent_symbol_key, indexed_at)
VALUES (@sid, @fid, @rid, 'src/Foo.cs::MyApp.Foo#class', 'Foo', 'MyApp.Foo', 'class',
        NULL, NULL, 1, 10, 0, 100, NULL, '2024-01-01T00:00:00Z');
";
        cmd.Parameters.Clear();
        cmd.Parameters.Add(new SqliteParameter("@rid", repoId.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@fid", fileId.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@hash", contentHash));
        cmd.Parameters.Add(new SqliteParameter("@sid", Guid.NewGuid().ToString()));
        cmd.ExecuteNonQuery();
    }
}
