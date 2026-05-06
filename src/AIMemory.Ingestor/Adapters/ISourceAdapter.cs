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
