namespace AIMemory.Models.Events;

public class CodeFileUpsertEvent
{
    public string RepositoryName { get; set; } = string.Empty;
    public string SourceType { get; set; } = "local";
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Legacy path field. v1 secondaries populated this with the project-root-relative path
    /// (forward-slash normalized). v2 secondaries populate <see cref="RelPath"/> instead;
    /// FilePath is kept on the wire so a v1 primary still understands the event.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Project-root-relative path, POSIX-style (forward slashes). Supersedes
    /// <see cref="FilePath"/> on v2 secondaries — see design doc §3.3. When both are
    /// populated, RelPath is authoritative; v2 primaries ignore FilePath.
    /// </summary>
    public string? RelPath { get; set; }

    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string? MachineName { get; set; }
}
