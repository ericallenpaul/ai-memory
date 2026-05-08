using AIMemory.Identity;

namespace AIMemory.Ingestor;

/// <summary>
/// Per-cycle identity context for the ingestor. Combines <see cref="IHostIdProvider"/> (single
/// value per process — cached forever) with a per-project lookup for <see cref="IProjectIdResolver"/>
/// — resolved once per source path per process, cached so the per-file hot path doesn't re-walk
/// libgit2 history.
/// </summary>
public interface IIngestorContext
{
    /// <summary>
    /// The lowercase-hex SHA-256 host id for this machine. Stable across runs.
    /// </summary>
    string HostId { get; }

    /// <summary>
    /// Returns the <see cref="ProjectIdentity"/> for the supplied watch path. The first call
    /// for a given path computes the identity (libgit2 walk for git repos, fallback otherwise);
    /// subsequent calls return the cached value. Thread-safe.
    /// </summary>
    ProjectIdentity GetProjectIdentity(string absolutePath);
}
