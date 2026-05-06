namespace AIMemory.Models.Events;

public class CodeSymbolBatchEvent
{
    public string RepositoryName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<CodeSymbolEvent> Symbols { get; set; } = [];
}

public class CodeSymbolEvent
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentSymbolKey { get; set; }
}
