namespace AIMemory.Models.Events;

public class CodeFileUpsertEvent
{
    public string RepositoryName { get; set; } = string.Empty;
    public string SourceType { get; set; } = "local";
    public string SourcePath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string? MachineName { get; set; }
}
