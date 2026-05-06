using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Models.Dtos;

namespace AIMemory.Ingestor.Adapters;

public interface ISourceAdapter
{
    string SourceName { get; }
    IEnumerable<string> DiscoverFiles(SourceConfig config);
    IEnumerable<RawRecord> ReadNewRecords(string filePath, Checkpoint? checkpoint);
    IEnumerable<IngestEvent> ParseRecord(RawRecord raw);

    /// <summary>
    /// Called once per source per scan cycle, after all per-file processing has finished.
    /// Adapters that need end-of-cycle work (reconciling deletions against the last-seen
    /// file set, advancing per-watchpath state, etc.) implement this. The default returns
    /// nothing — log/transcript adapters don't need it.
    /// </summary>
    /// <remarks>
    /// The <paramref name="enumeratedFilesByWatchPath"/> map gives each adapter the list
    /// of files DiscoverFiles yielded for each watch path during the just-completed scan,
    /// so it doesn't have to re-walk to compute deletions.
    /// </remarks>
    IEnumerable<IngestEvent> Reconcile(
        SourceConfig config,
        IReadOnlyDictionary<string, IReadOnlyList<string>> enumeratedFilesByWatchPath)
        => Array.Empty<IngestEvent>();
}

public class RawRecord
{
    public string FilePath { get; set; } = string.Empty;
    public long Offset { get; set; }
    public string Line { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Segment { get; set; } = "personal";
    public string? ContentHash { get; set; }
}
