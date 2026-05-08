using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Code-index data access. Post-phase-6, projects (cross-host identity) replace the legacy
/// <c>code_repositories</c> table; files are content-addressed (<see cref="CodeFile"/> keyed
/// by <c>ContentSha256</c>); per-host filesystem location lives in <see cref="FileLocation"/>.
///
/// <para>The legacy <c>Guid RepositoryId</c> is gone — project ids are 64-character lowercase
/// hex SHA-256 strings derived by <c>AIMemory.Identity.ProjectIdResolver</c>. Methods that
/// used to take a <c>Guid</c> repository id now take a <c>string</c> project id.</para>
/// </summary>
public interface ICodeIndexRepository
{
    // ---------- Projects ----------

    /// <summary>
    /// Inserts or updates a <see cref="Project"/> row, keyed by <see cref="Project.ProjectId"/>.
    /// Returns the persisted entity (with <c>FirstSeenAt</c>/<c>LastSeenAt</c> populated).
    /// </summary>
    Task<Project> UpsertProjectAsync(Project project);

    /// <summary>
    /// Gets a project by id. Returns null if not present.
    /// </summary>
    Task<Project?> GetProjectAsync(string projectId);

    /// <summary>
    /// Gets a project by display name. Used during ingest when the secondary doesn't know
    /// the project id (legacy ingest path) — for new flows, prefer the id form.
    /// </summary>
    Task<Project?> GetProjectByNameAsync(string displayName);

    /// <summary>
    /// Lists all projects ordered by most recently seen.
    /// </summary>
    Task<List<Project>> ListProjectsAsync();

    /// <summary>
    /// Deletes a project and all dependent rows: file_locations, code_symbols, and any
    /// orphaned code_files (content blobs no longer referenced by any location).
    /// </summary>
    Task DeleteProjectAsync(string projectId);

    /// <summary>
    /// Recomputes <c>FileCount</c>/<c>SymbolCount</c> for a project from the location/symbol tables.
    /// Counts file_locations on the local host only.
    /// </summary>
    Task UpdateProjectStatsAsync(string projectId, string localHostId);

    // ---------- Files ----------

    /// <summary>
    /// Upserts a content blob and a file location pointing at it. Idempotent — same
    /// (host, project, path, content) is a no-op.
    /// </summary>
    Task UpsertFileAsync(string hostId, string projectId, string relPath, string language,
        long fileSize, string contentSha256);

    /// <summary>
    /// Removes file_locations for paths in <paramref name="projectId"/> that are no longer
    /// present on the host. Garbage-collects orphan code_files (content blobs no longer
    /// referenced by any location after the delete).
    /// </summary>
    Task DeleteStaleFilesAsync(string hostId, string projectId, IEnumerable<string> currentRelPaths);

    /// <summary>
    /// Removes a single file_location and any orphaned content. Returns true if a row was deleted.
    /// </summary>
    Task<bool> DeleteFileAsync(string hostId, string projectId, string relPath);

    /// <summary>
    /// Returns this host's view of the project tree — one entry per file_location.
    /// </summary>
    Task<List<FileLocation>> GetFileTreeAsync(string projectId, string hostId);

    // ---------- Symbols ----------

    /// <summary>
    /// Replaces all symbols for the given content blob (which is by definition shared
    /// across all hosts that have indexed it). Symbol identity is content-keyed, not host-keyed.
    /// </summary>
    Task UpsertSymbolsAsync(string contentSha256, string projectId, List<CodeSymbol> symbols);

    /// <summary>
    /// Gets symbols owned by the content blob currently mapped to <paramref name="relPath"/>
    /// on the given host. Returns an empty list if the file is unknown to this host.
    /// </summary>
    Task<List<CodeSymbol>> GetFileOutlineAsync(string projectId, string hostId, string relPath);

    Task<CodeSymbol?> GetSymbolByKeyAsync(string projectId, string symbolKey);

    Task<List<CodeSymbol>> GetSymbolsByKeysAsync(string projectId, List<string> symbolKeys);

    Task<List<CodeSymbol>> SearchSymbolsAsync(string query, string? projectId, string? kind, int limit);

    /// <summary>
    /// Resolves <c>(projectId, symbolKey)</c> to the rel-path the symbol's content currently
    /// lives at on <paramref name="hostId"/>. Returns null if the host doesn't have the content.
    /// </summary>
    Task<string?> ResolveSymbolRelPathAsync(string projectId, string symbolKey, string hostId);
}
