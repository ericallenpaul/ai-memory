namespace AIMemory.Models.Entities;

public class CodeRepository
{
    public Guid RepositoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceType { get; set; } = "local"; // "local" | "github"
    public string SourcePath { get; set; } = string.Empty;
    public string? DefaultBranch { get; set; }
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<CodeFile> Files { get; set; } = [];
}
