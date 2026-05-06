namespace AIMemory.CodeIndex.Parsers;

public interface ILanguageParser
{
    string Language { get; }
    string[] FileExtensions { get; }
    List<ParsedSymbol> Parse(string filePath, string content);
}

public class ParsedSymbol
{
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public string? ParentName { get; set; }
}
