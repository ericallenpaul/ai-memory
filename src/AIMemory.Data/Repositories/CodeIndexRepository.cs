using Microsoft.EntityFrameworkCore;
using AIMemory.Models.Entities;

namespace AIMemory.Data.Repositories;

/// <summary>
/// Phase-6 content-addressed implementation of <see cref="ICodeIndexRepository"/>.
/// </summary>
public class CodeIndexRepository : ICodeIndexRepository
{
    private readonly AIMemoryDbContext _db;

    public CodeIndexRepository(AIMemoryDbContext db)
    {
        _db = db;
    }

    // ---------- Projects ----------

    public async Task<Project> UpsertProjectAsync(Project project)
    {
        if (string.IsNullOrEmpty(project.ProjectId))
            throw new ArgumentException("ProjectId must be set before upsert", nameof(project));

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == project.ProjectId);
        if (existing != null)
        {
            existing.DisplayName = project.DisplayName;
            existing.CanonicalRemoteUrl = project.CanonicalRemoteUrl ?? existing.CanonicalRemoteUrl;
            existing.RootCommitSha = project.RootCommitSha ?? existing.RootCommitSha;
            existing.IdentityKind = project.IdentityKind;
            existing.SourceType = project.SourceType;
            existing.SourcePath = project.SourcePath;
            existing.DefaultBranch = project.DefaultBranch ?? existing.DefaultBranch;
            existing.LastSeenAt = now;
        }
        else
        {
            project.FirstSeenAt = now;
            project.LastSeenAt = now;
            _db.Projects.Add(project);
            existing = project;
        }

        await _db.SaveChangesAsync();
        return existing;
    }

    public Task<Project?> GetProjectAsync(string projectId)
        => _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId);

    public Task<Project?> GetProjectByNameAsync(string displayName)
        => _db.Projects.FirstOrDefaultAsync(p => p.DisplayName == displayName);

    public Task<List<Project>> ListProjectsAsync()
        => _db.Projects.OrderByDescending(p => p.LastSeenAt).ToListAsync();

    public async Task DeleteProjectAsync(string projectId)
    {
        // Symbols are project-scoped — drop them first.
        var symbols = _db.CodeSymbols.Where(s => s.ProjectId == projectId);
        _db.CodeSymbols.RemoveRange(symbols);

        // file_locations are project-scoped (across all hosts).
        var locations = await _db.FileLocations.Where(l => l.ProjectId == projectId).ToListAsync();
        _db.FileLocations.RemoveRange(locations);

        var project = await _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId);
        if (project != null)
            _db.Projects.Remove(project);

        await _db.SaveChangesAsync();

        // GC orphan content blobs that no other project references.
        var orphanHashes = locations.Select(l => l.ContentSha256).Distinct().ToList();
        await DeleteOrphanContentAsync(orphanHashes);
    }

    public async Task UpdateProjectStatsAsync(string projectId, string localHostId)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.ProjectId == projectId);
        if (project == null) return;

        project.FileCount = await _db.FileLocations
            .CountAsync(l => l.ProjectId == projectId && l.HostId == localHostId);
        project.SymbolCount = await _db.CodeSymbols.CountAsync(s => s.ProjectId == projectId);
        project.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ---------- Files ----------

    public async Task UpsertFileAsync(string hostId, string projectId, string relPath, string language,
        long fileSize, string contentSha256)
    {
        if (string.IsNullOrEmpty(contentSha256))
            throw new ArgumentException("contentSha256 must be set", nameof(contentSha256));

        var now = DateTimeOffset.UtcNow;

        // 1. Content blob (shared across hosts).
        var content = await _db.CodeFiles.FirstOrDefaultAsync(f => f.ContentSha256 == contentSha256);
        if (content == null)
        {
            _db.CodeFiles.Add(new CodeFile
            {
                ContentSha256 = contentSha256,
                Language = language,
                FileSize = fileSize,
                FirstSeenAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            content.LastSeenAt = now;
        }

        // 2. Per-host location.
        var location = await _db.FileLocations.FirstOrDefaultAsync(l =>
            l.HostId == hostId && l.ProjectId == projectId && l.RelPath == relPath);

        string? oldContent = null;
        if (location == null)
        {
            _db.FileLocations.Add(new FileLocation
            {
                HostId = hostId,
                ProjectId = projectId,
                RelPath = relPath,
                ContentSha256 = contentSha256,
                Language = language,
                FileSize = fileSize,
                FirstSeenAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            if (location.ContentSha256 != contentSha256)
                oldContent = location.ContentSha256;
            location.ContentSha256 = contentSha256;
            location.Language = language;
            location.FileSize = fileSize;
            location.LastSeenAt = now;
        }

        await _db.SaveChangesAsync();

        // 3. GC the previously-pointed-to blob if no one references it now.
        if (oldContent != null)
            await DeleteOrphanContentAsync(new[] { oldContent });
    }

    public async Task DeleteStaleFilesAsync(string hostId, string projectId, IEnumerable<string> currentRelPaths)
    {
        var pathSet = currentRelPaths.ToHashSet();
        var staleLocations = await _db.FileLocations
            .Where(l => l.HostId == hostId && l.ProjectId == projectId)
            .ToListAsync();

        var toRemove = staleLocations.Where(l => !pathSet.Contains(l.RelPath)).ToList();
        if (toRemove.Count == 0) return;

        var orphanCandidates = toRemove.Select(l => l.ContentSha256).Distinct().ToList();
        _db.FileLocations.RemoveRange(toRemove);
        await _db.SaveChangesAsync();

        await DeleteOrphanContentAsync(orphanCandidates);
    }

    public async Task<bool> DeleteFileAsync(string hostId, string projectId, string relPath)
    {
        var location = await _db.FileLocations.FirstOrDefaultAsync(l =>
            l.HostId == hostId && l.ProjectId == projectId && l.RelPath == relPath);
        if (location == null) return false;

        var contentHash = location.ContentSha256;
        _db.FileLocations.Remove(location);
        await _db.SaveChangesAsync();

        await DeleteOrphanContentAsync(new[] { contentHash });
        return true;
    }

    public Task<List<FileLocation>> GetFileTreeAsync(string projectId, string hostId)
    {
        return _db.FileLocations
            .Where(l => l.ProjectId == projectId && l.HostId == hostId)
            .OrderBy(l => l.RelPath)
            .ToListAsync();
    }

    // ---------- Symbols ----------

    public async Task UpsertSymbolsAsync(string contentSha256, string projectId, List<CodeSymbol> symbols)
    {
        // Replace symbols for this content blob within this project. (A symbol's identity is
        // content + project — re-keying ensures the per-project symbol_key stays stable when
        // the same content is also indexed under a different project.)
        var existing = _db.CodeSymbols
            .Where(s => s.ContentSha256 == contentSha256 && s.ProjectId == projectId);
        _db.CodeSymbols.RemoveRange(existing);

        foreach (var symbol in symbols)
        {
            symbol.SymbolId = Guid.NewGuid();
            symbol.ContentSha256 = contentSha256;
            symbol.ProjectId = projectId;
            symbol.IndexedAt = DateTimeOffset.UtcNow;
        }

        _db.CodeSymbols.AddRange(symbols);
        await _db.SaveChangesAsync();
    }

    public async Task<List<CodeSymbol>> GetFileOutlineAsync(string projectId, string hostId, string relPath)
    {
        var location = await _db.FileLocations.FirstOrDefaultAsync(l =>
            l.ProjectId == projectId && l.HostId == hostId && l.RelPath == relPath);
        if (location == null) return [];

        return await _db.CodeSymbols
            .Where(s => s.ProjectId == projectId && s.ContentSha256 == location.ContentSha256)
            .OrderBy(s => s.StartLine)
            .ToListAsync();
    }

    public Task<CodeSymbol?> GetSymbolByKeyAsync(string projectId, string symbolKey)
        => _db.CodeSymbols.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.SymbolKey == symbolKey);

    public Task<List<CodeSymbol>> GetSymbolsByKeysAsync(string projectId, List<string> symbolKeys)
        => _db.CodeSymbols
            .Where(s => s.ProjectId == projectId && symbolKeys.Contains(s.SymbolKey))
            .ToListAsync();

    public async Task<List<CodeSymbol>> SearchSymbolsAsync(string query, string? projectId, string? kind, int limit)
    {
        var q = _db.CodeSymbols.AsQueryable();

        if (!string.IsNullOrEmpty(projectId))
            q = q.Where(s => s.ProjectId == projectId);

        if (!string.IsNullOrEmpty(kind))
            q = q.Where(s => s.Kind == kind);

        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(s => EF.Functions.Like(s.Name, $"%{query}%")
                          || EF.Functions.Like(s.QualifiedName, $"%{query}%"));
        }

        return await q.OrderBy(s => s.Name).Take(limit).ToListAsync();
    }

    public async Task<string?> ResolveSymbolRelPathAsync(string projectId, string symbolKey, string hostId)
    {
        var sym = await _db.CodeSymbols
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.SymbolKey == symbolKey);
        if (sym == null) return null;

        var loc = await _db.FileLocations
            .Where(l => l.ProjectId == projectId
                     && l.HostId == hostId
                     && l.ContentSha256 == sym.ContentSha256)
            .OrderBy(l => l.RelPath)
            .FirstOrDefaultAsync();
        return loc?.RelPath;
    }

    // ---------- Internals ----------

    private async Task DeleteOrphanContentAsync(IEnumerable<string> hashes)
    {
        var distinct = hashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (distinct.Count == 0) return;

        // A blob is orphan iff no file_location currently references it.
        var stillReferenced = await _db.FileLocations
            .Where(l => distinct.Contains(l.ContentSha256))
            .Select(l => l.ContentSha256)
            .Distinct()
            .ToListAsync();

        var toDelete = distinct.Except(stillReferenced).ToList();
        if (toDelete.Count == 0) return;

        // Drop symbols owned by orphan content (across all projects).
        var orphanSymbols = _db.CodeSymbols.Where(s => toDelete.Contains(s.ContentSha256));
        _db.CodeSymbols.RemoveRange(orphanSymbols);

        var orphanFiles = _db.CodeFiles.Where(f => toDelete.Contains(f.ContentSha256));
        _db.CodeFiles.RemoveRange(orphanFiles);

        await _db.SaveChangesAsync();
    }
}
