namespace AIMemory.Models.Events;

/// <summary>
/// Emitted when the ingestor observes that a previously-indexed file no longer exists,
/// either because git diff shows it deleted or because a filesystem reconciliation found
/// it missing from disk. Causes the API to remove the file row and cascade-delete its
/// symbols.
/// </summary>
public class CodeFileDeleteEvent
{
    public string RepositoryName { get; set; } = string.Empty;

    /// <summary>
    /// Legacy path field — see <c>CodeFileUpsertEvent.FilePath</c> for the same compat note.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Project-root-relative path, POSIX-style. Supersedes <see cref="FilePath"/> on v2.
    /// </summary>
    public string? RelPath { get; set; }

    public string? MachineName { get; set; }
}
