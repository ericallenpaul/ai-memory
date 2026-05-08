using AIMemory.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIMemory.Data.Migrations;

/// <summary>
/// Runs once after <c>AddDistributedIdentity</c> applies. The migration leaves projects
/// with synthetic <c>identity_kind = 'fallback'</c> ids; this class re-runs each project
/// through <see cref="IProjectIdResolver"/> and migrates the rows onto the canonical id
/// when the source path is a non-shallow git repo with a remote.
///
/// <para>Idempotent — gated on the <c>__post_migrate_pending</c> marker that the migration
/// inserts. After successful run, the marker is deleted.</para>
/// </summary>
public sealed class LegacyProjectMigrator
{
    private const string PendingKind = "seed_projects_from_legacy";

    private readonly AIMemoryDbContext _db;
    private readonly IProjectIdResolver _resolver;
    private readonly IHostIdProvider _hostIds;
    private readonly ILogger<LegacyProjectMigrator> _logger;

    public LegacyProjectMigrator(
        AIMemoryDbContext db,
        IProjectIdResolver resolver,
        IHostIdProvider hostIds,
        ILogger<LegacyProjectMigrator>? logger = null)
    {
        _db = db;
        _resolver = resolver;
        _hostIds = hostIds;
        _logger = logger ?? NullLogger<LegacyProjectMigrator>.Instance;
    }

    /// <summary>
    /// Runs the post-migrate seed if pending. No-op if the marker isn't present (so it's
    /// safe to call on every API startup).
    /// </summary>
    public async Task RunIfPendingAsync(CancellationToken ct = default)
    {
        if (!await IsPendingAsync(ct)) return;

        var hostId = _hostIds.GetHostId();
        var fallbacks = await _db.Projects
            .Where(p => p.IdentityKind == "fallback")
            .ToListAsync(ct);

        if (fallbacks.Count == 0)
        {
            _logger.LogDebug("No fallback projects to canonicalize");
            await ClearPendingAsync(ct);
            return;
        }

        _logger.LogInformation("Canonicalizing {Count} legacy project(s) to git identity where possible",
            fallbacks.Count);

        foreach (var legacy in fallbacks)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var resolved = _resolver.Resolve(legacy.SourcePath, hostId);

                // If the resolver also returned a fallback (e.g. path missing, no remote),
                // upgrade the project_id to the canonical fallback id (the synthetic
                // GUID-doubled id from the migration is not a real sha256 — replace it).
                if (resolved.ProjectId == legacy.ProjectId) continue;

                await MigrateProjectIdAsync(legacy.ProjectId, resolved, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to canonicalize project '{Name}' at {Path}; leaving as fallback",
                    legacy.DisplayName, legacy.SourcePath);
            }
        }

        await ClearPendingAsync(ct);
    }

    private async Task<bool> IsPendingAsync(CancellationToken ct)
    {
        // Check via raw SQL — the __post_migrate_pending table isn't part of the EF model.
        var conn = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__post_migrate_pending';";
            var tableExists = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
            if (!tableExists) return false;

            cmd.CommandText = "SELECT COUNT(*) FROM __post_migrate_pending WHERE pending_kind = @k;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@k"; p.Value = PendingKind;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    private async Task ClearPendingAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM __post_migrate_pending WHERE pending_kind = @k;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@k"; p.Value = PendingKind;
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Atomically migrates a project from <paramref name="oldProjectId"/> to
    /// <paramref name="resolved"/>. Updates <c>file_locations.project_id</c> and
    /// <c>code_symbols.project_id</c>/<c>symbol_key</c>/<c>parent_symbol_key</c> to point at
    /// the new id. If a project with the new id already exists (e.g. the same git repo was
    /// indexed twice under different display names), merges file_locations into it.
    /// </summary>
    private async Task MigrateProjectIdAsync(string oldProjectId, ProjectIdentity resolved, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var existing = await _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == resolved.ProjectId, ct);
        var legacy = await _db.Projects.FirstAsync(p => p.ProjectId == oldProjectId, ct);

        if (existing == null)
        {
            // Insert a fresh project row at the new id (copy display_name etc), drop the old.
            _db.Projects.Add(new Models.Entities.Project
            {
                ProjectId = resolved.ProjectId,
                DisplayName = legacy.DisplayName,
                CanonicalRemoteUrl = resolved.CanonicalRemoteUrl,
                RootCommitSha = resolved.RootCommitSha,
                IdentityKind = resolved.IdentityKind,
                SourceType = legacy.SourceType,
                SourcePath = legacy.SourcePath,
                DefaultBranch = legacy.DefaultBranch,
                FileCount = legacy.FileCount,
                SymbolCount = legacy.SymbolCount,
                FirstSeenAt = legacy.FirstSeenAt,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            // Same project already exists at the canonical id. Merge stats; file_locations and
            // symbols will move below via raw SQL UPDATE.
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // Move file_locations onto the new project_id. ON CONFLICT IGNORE preserves any
        // existing rows on the merged target (canonical id already had this rel_path).
        await _db.Database.ExecuteSqlRawAsync(@"
INSERT OR IGNORE INTO file_locations (host_id, project_id, rel_path, content_sha256, language, file_size, first_seen_at, last_seen_at)
SELECT host_id, {0}, rel_path, content_sha256, language, file_size, first_seen_at, last_seen_at
FROM file_locations WHERE project_id = {1};
DELETE FROM file_locations WHERE project_id = {1};",
            resolved.ProjectId, oldProjectId);

        // Move symbols. symbol_key prefix changes from old project_id::… → new project_id::….
        await _db.Database.ExecuteSqlRawAsync(@"
UPDATE code_symbols
SET project_id = {0},
    symbol_key = {0} || SUBSTR(symbol_key, INSTR(symbol_key, '::')),
    parent_symbol_key = CASE
        WHEN parent_symbol_key IS NULL OR parent_symbol_key = '' THEN NULL
        ELSE {0} || SUBSTR(parent_symbol_key, INSTR(parent_symbol_key, '::'))
    END
WHERE project_id = {1};",
            resolved.ProjectId, oldProjectId);

        // Drop the legacy project row.
        _db.Projects.Remove(legacy);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation("Project '{Name}' migrated: {Old} → {New} (kind={Kind})",
            legacy.DisplayName, oldProjectId, resolved.ProjectId, resolved.IdentityKind);
    }
}
