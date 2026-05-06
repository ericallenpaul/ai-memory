namespace AIMemory.Models.Entities;

public class CodeFile
{
    public Guid FileId { get; set; }
    public Guid RepositoryId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; }

    public CodeRepository? Repository { get; set; }
    public List<CodeSymbol> Symbols { get; set; } = [];
}
