using System.Collections.Concurrent;
using AIMemory.Identity;

namespace AIMemory.Ingestor;

/// <summary>
/// Default <see cref="IIngestorContext"/>. <see cref="HostId"/> is computed once from
/// <see cref="IHostIdProvider"/> (which itself caches). Project lookups are cached in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the normalized absolute path, so
/// a multi-source-thread scan over the same repo only walks libgit2 once.
/// </summary>
public sealed class IngestorContext : IIngestorContext
{
    private readonly IHostIdProvider _hostIdProvider;
    private readonly IProjectIdResolver _projectIdResolver;
    private readonly ConcurrentDictionary<string, ProjectIdentity> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _hostIdCache;
    private readonly object _hostIdLock = new();

    public IngestorContext(IHostIdProvider hostIdProvider, IProjectIdResolver projectIdResolver)
    {
        _hostIdProvider = hostIdProvider ?? throw new ArgumentNullException(nameof(hostIdProvider));
        _projectIdResolver = projectIdResolver ?? throw new ArgumentNullException(nameof(projectIdResolver));
    }

    public string HostId
    {
        get
        {
            if (_hostIdCache != null) return _hostIdCache;
            lock (_hostIdLock)
            {
                _hostIdCache ??= _hostIdProvider.GetHostId();
                return _hostIdCache;
            }
        }
    }

    public ProjectIdentity GetProjectIdentity(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("absolutePath must be non-empty", nameof(absolutePath));

        // Normalize once so two scans of the same repo via different relative paths share the cache entry.
        var key = Path.GetFullPath(absolutePath);
        return _projectCache.GetOrAdd(key, p => _projectIdResolver.Resolve(p, HostId));
    }
}
