namespace AIMemory.Models.Entities;

public class CodeSymbol
{
    public Guid SymbolId { get; set; }
    public Guid FileId { get; set; }
    public Guid RepositoryId { get; set; }
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public string? Summary { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentSymbolKey { get; set; }
    public DateTimeOffset IndexedAt { get; set; }

    public CodeFile? File { get; set; }
}
