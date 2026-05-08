using System;
using System.Globalization;
using AIMemory.Identity;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIMemory.Data.Migrations
{
    /// <summary>
    /// Phase 6: introduces cross-host project + host identity.
    ///
    /// <para>This migration is forward-only. <c>Down</c> throws — too much data has been
    /// transformed for a meaningful rollback (consistent with previous AIMemory migrations).</para>
    ///
    /// <para>Up sequence:
    /// <list type="number">
    /// <item>Create new tables (<c>hosts</c>, <c>projects</c>, <c>file_locations</c>, <c>pairings</c>).</item>
    /// <item>Backfill: synthesize the local <c>host</c> row, derive one <c>project</c> per
    ///       legacy <c>code_repository</c>, derive one <c>file_location</c> per legacy
    ///       <c>code_file</c>. Project ids are deterministic synthetic ids during this
    ///       step; <see cref="LegacyProjectMigrator"/> upgrades them to canonical git ids
    ///       (per design doc §2.2) at API startup, in the same <c>Database.Migrate()</c>
    ///       cycle, where libgit2 + DI are available.</item>
    /// <item>Recreate <c>code_files</c> (now content-addressed) and <c>code_symbols</c>
    ///       (re-keyed onto <c>(content_sha256, project_id)</c> with the new
    ///       <c>project_id::QualifiedName#kind</c> symbol_key format).</item>
    /// <item>Drop the legacy <c>code_repositories</c> table; create a SQLite VIEW of the
    ///       same name over <c>projects</c> for read-back compatibility.</item>
    /// </list>
    /// </para>
    /// </summary>
    public partial class AddDistributedIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateNewTables(migrationBuilder);
            CreateNewIndexes(migrationBuilder);
            BackfillIdentity(migrationBuilder);
            RecreateCodeIndexTables(migrationBuilder);
            DropLegacyAndCreateView(migrationBuilder);
        }

        // ---- 1. New tables for distributed identity. ---------------------------------------

        private static void CreateNewTables(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    host_id = table.Column<string>(type: "TEXT", nullable: false),
                    friendly_name = table.Column<string>(type: "TEXT", nullable: false),
                    os_kind = table.Column<string>(type: "TEXT", nullable: false),
                    is_local = table.Column<bool>(type: "INTEGER", nullable: false),
                    first_seen_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_hosts", x => x.host_id));

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    canonical_remote_url = table.Column<string>(type: "TEXT", nullable: true),
                    root_commit_sha = table.Column<string>(type: "TEXT", nullable: true),
                    identity_kind = table.Column<string>(type: "TEXT", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", nullable: false),
                    source_path = table.Column<string>(type: "TEXT", nullable: false),
                    default_branch = table.Column<string>(type: "TEXT", nullable: true),
                    file_count = table.Column<int>(type: "INTEGER", nullable: false),
                    symbol_count = table.Column<int>(type: "INTEGER", nullable: false),
                    first_seen_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_projects", x => x.project_id));

            migrationBuilder.CreateTable(
                name: "file_locations",
                columns: table => new
                {
                    host_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    rel_path = table.Column<string>(type: "TEXT", nullable: false),
                    content_sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    language = table.Column<string>(type: "TEXT", nullable: false),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false),
                    first_seen_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_file_locations",
                    x => new { x.host_id, x.project_id, x.rel_path }));

            migrationBuilder.CreateTable(
                name: "pairings",
                columns: table => new
                {
                    pairing_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    host_id = table.Column<string>(type: "TEXT", nullable: false),
                    api_key_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    friendly_name = table.Column<string>(type: "TEXT", nullable: false),
                    paired_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_contact_at = table.Column<string>(type: "TEXT", nullable: true),
                    is_revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_pairings", x => x.pairing_id));
        }

        private static void CreateNewIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_hosts_friendly_name",
                table: "hosts",
                column: "friendly_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_canonical_remote_url",
                table: "projects",
                column: "canonical_remote_url");

            migrationBuilder.CreateIndex(
                name: "IX_projects_display_name",
                table: "projects",
                column: "display_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_locations_content_sha256",
                table: "file_locations",
                column: "content_sha256");

            migrationBuilder.CreateIndex(
                name: "IX_file_locations_project_id",
                table: "file_locations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_pairings_host_id",
                table: "pairings",
                column: "host_id",
                unique: true,
                filter: "is_revoked = 0");
        }

        // ---- 2. Backfill: hosts, projects, file_locations from legacy data. ----------------

        private static void BackfillIdentity(MigrationBuilder migrationBuilder)
        {
            var saltStore = InstallSaltStore.CreateDefault();
            var hostIdProvider = new HostIdProvider(saltStore);

            var hostId = hostIdProvider.GetHostId();
            var osKind = hostIdProvider.GetOsKind();
            var friendlyName = Environment.MachineName;
            var nowIso = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Local Host. INSERT OR IGNORE makes this idempotent across re-runs in dev.
            migrationBuilder.Sql(
                $"INSERT OR IGNORE INTO hosts (host_id, friendly_name, os_kind, is_local, first_seen_at, last_seen_at) " +
                $"VALUES ({Quote(hostId)}, {Quote(friendlyName)}, {Quote(osKind)}, 1, {Quote(nowIso)}, {Quote(nowIso)});");

            // Mark canonical-id derivation as pending — LegacyProjectMigrator runs this at
            // API startup, where it can use libgit2 + DI to compute proper project ids per
            // design doc §2.2 and merge fallback projects into their git equivalents.
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS __post_migrate_pending (
    pending_kind TEXT NOT NULL PRIMARY KEY,
    queued_at    TEXT NOT NULL
);");
            migrationBuilder.Sql(
                $"INSERT OR IGNORE INTO __post_migrate_pending (pending_kind, queued_at) " +
                $"VALUES ('seed_projects_from_legacy', {Quote(nowIso)});");

            // repository_id → synthetic project_id mapping. The synthetic id is a 64-char
            // hex string (the GUID's hex doubled) — semantically valid for the column shape
            // and stable per repository_id. LegacyProjectMigrator replaces these with
            // canonical sha256 ids on the next API startup.
            migrationBuilder.Sql(@"
CREATE TABLE __repo_to_project (
    repository_id TEXT NOT NULL PRIMARY KEY,
    project_id    TEXT NOT NULL
);");

            migrationBuilder.Sql(@"
INSERT INTO __repo_to_project (repository_id, project_id)
SELECT
    repository_id,
    LOWER(REPLACE(repository_id, '-', '')) ||
    LOWER(REPLACE(repository_id, '-', ''))   AS project_id
FROM code_repositories;");

            migrationBuilder.Sql(@"
INSERT OR IGNORE INTO projects (
    project_id, display_name, canonical_remote_url, root_commit_sha,
    identity_kind, source_type, source_path, default_branch,
    file_count, symbol_count, first_seen_at, last_seen_at
)
SELECT
    rmap.project_id,
    cr.name,
    NULL,
    NULL,
    'fallback',
    cr.source_type,
    cr.source_path,
    cr.default_branch,
    cr.file_count,
    cr.symbol_count,
    cr.indexed_at,
    cr.updated_at
FROM code_repositories cr
JOIN __repo_to_project rmap ON rmap.repository_id = cr.repository_id;");

            // file_locations from existing code_files. relPath = old file_path. content_sha256
            // comes from old content_hash. Skip rows with empty content_hash (shouldn't happen
            // in real data, but defensive).
            migrationBuilder.Sql(
                $"INSERT OR IGNORE INTO file_locations (host_id, project_id, rel_path, content_sha256, language, file_size, first_seen_at, last_seen_at) " +
                $"SELECT {Quote(hostId)}, rmap.project_id, f.file_path, f.content_hash, f.language, f.file_size, f.indexed_at, f.indexed_at " +
                $"FROM code_files f " +
                $"JOIN __repo_to_project rmap ON rmap.repository_id = f.repository_id " +
                $"WHERE f.content_hash IS NOT NULL AND f.content_hash <> '';");
        }

        // ---- 3. Recreate code_files (content-addressed) and code_symbols. ------------------

        private static void RecreateCodeIndexTables(MigrationBuilder migrationBuilder)
        {
            // SQLite supports column drops since 3.35 but mixing drops + PK changes is fragile.
            // The clean path is "create new, copy, swap" via temp tables.
            //
            // ALTER TABLE … RENAME TO leaves indexes attached to the renamed table — and
            // SQLite indexes share a global namespace, so we must drop them before recreating
            // indexes of the same name on the fresh table below.
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_files_language;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_files_repository_id;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_files_repository_id_file_path;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_files_CodeRepositoryRepositoryId;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_file_id;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_repository_id;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_symbol_key;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_kind;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_name;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_qualified_name;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_code_symbols_CodeFileFileId;");

            migrationBuilder.Sql("ALTER TABLE code_files RENAME TO code_files_old;");
            migrationBuilder.Sql("ALTER TABLE code_symbols RENAME TO code_symbols_old;");

            migrationBuilder.Sql(@"
CREATE TABLE code_files (
    content_sha256 TEXT NOT NULL PRIMARY KEY,
    language       TEXT NOT NULL,
    file_size      INTEGER NOT NULL,
    first_seen_at  TEXT NOT NULL,
    last_seen_at   TEXT NOT NULL
);");
            migrationBuilder.Sql("CREATE INDEX IX_code_files_language ON code_files(language);");

            // Coalesce duplicate content_hashes (a single content blob may have lived under
            // multiple paths) by taking the earliest indexed_at.
            migrationBuilder.Sql(@"
INSERT INTO code_files (content_sha256, language, file_size, first_seen_at, last_seen_at)
SELECT
    content_hash    AS content_sha256,
    MIN(language)   AS language,
    MIN(file_size)  AS file_size,
    MIN(indexed_at) AS first_seen_at,
    MAX(indexed_at) AS last_seen_at
FROM code_files_old
WHERE content_hash IS NOT NULL AND content_hash <> ''
GROUP BY content_hash;");

            migrationBuilder.Sql(@"
CREATE TABLE code_symbols (
    symbol_id          TEXT NOT NULL PRIMARY KEY,
    content_sha256     TEXT NOT NULL,
    project_id         TEXT NOT NULL,
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
);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_content_sha256  ON code_symbols(content_sha256);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_project_id      ON code_symbols(project_id);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_symbol_key      ON code_symbols(symbol_key);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_kind            ON code_symbols(kind);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_name            ON code_symbols(name);");
            migrationBuilder.Sql("CREATE INDEX IX_code_symbols_qualified_name  ON code_symbols(qualified_name);");

            // Re-key symbols. Old (file_id, repository_id) → new (content_sha256, project_id).
            // symbol_key changes from "filepath::Q#k" → "project_id::Q#k" (per design doc §1.5).
            migrationBuilder.Sql(@"
INSERT INTO code_symbols (symbol_id, content_sha256, project_id, symbol_key, name, qualified_name,
                          kind, signature, summary, start_line, end_line, start_byte, end_byte,
                          parent_symbol_key, indexed_at)
SELECT
    s.symbol_id,
    f.content_hash                                              AS content_sha256,
    rmap.project_id                                             AS project_id,
    rmap.project_id || '::' || s.qualified_name || '#' || s.kind AS symbol_key,
    s.name,
    s.qualified_name,
    s.kind,
    s.signature,
    s.summary,
    s.start_line,
    s.end_line,
    s.start_byte,
    s.end_byte,
    CASE
        WHEN s.parent_symbol_key IS NULL OR s.parent_symbol_key = '' THEN NULL
        -- Old format: filepath::QualifiedName#kind. Transform path prefix → project_id.
        ELSE rmap.project_id || '::' ||
             SUBSTR(s.parent_symbol_key, INSTR(s.parent_symbol_key, '::') + 2)
    END                                                          AS parent_symbol_key,
    s.indexed_at
FROM code_symbols_old s
JOIN code_files_old   f    ON f.file_id = s.file_id
JOIN __repo_to_project rmap ON rmap.repository_id = s.repository_id
WHERE f.content_hash IS NOT NULL AND f.content_hash <> '';");

            migrationBuilder.Sql("DROP TABLE code_symbols_old;");
            migrationBuilder.Sql("DROP TABLE code_files_old;");
            migrationBuilder.Sql("DROP TABLE __repo_to_project;");
        }

        // ---- 4. Drop legacy table; create compatibility view. ------------------------------

        private static void DropLegacyAndCreateView(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE code_repositories;");

            // SQLite views are read-only; all write paths to code_repositories were refactored
            // to write to projects in this same release. The `repository_id` column maps to
            // `project_id` (a 64-char hex string in v2; legacy GUIDs no longer apply).
            migrationBuilder.Sql(@"
CREATE VIEW code_repositories AS
SELECT
    project_id     AS repository_id,
    display_name   AS name,
    source_type    AS source_type,
    source_path    AS source_path,
    default_branch AS default_branch,
    file_count     AS file_count,
    symbol_count   AS symbol_count,
    first_seen_at  AS indexed_at,
    last_seen_at   AS updated_at
FROM projects;");
        }

        private static string Quote(string s) =>
            "'" + (s ?? string.Empty).Replace("'", "''") + "'";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "AddDistributedIdentity is forward-only. Restore from backup to roll back.");
        }
    }
}
