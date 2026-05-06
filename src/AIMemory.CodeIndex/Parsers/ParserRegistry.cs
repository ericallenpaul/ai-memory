namespace AIMemory.CodeIndex.Parsers;

public class ParserRegistry
{
    private readonly Dictionary<string, ILanguageParser> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public ParserRegistry(IEnumerable<ILanguageParser> parsers)
    {
        foreach (var parser in parsers)
        {
            foreach (var ext in parser.FileExtensions)
            {
                _byExtension[ext] = parser;
            }
        }
    }

    public ILanguageParser? GetParser(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;
        _byExtension.TryGetValue(ext, out var parser);
        return parser;
    }

    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _byExtension.ContainsKey(ext);
    }

    public string GetLanguage(string filePath)
    {
        var parser = GetParser(filePath);
        return parser?.Language ?? "unknown";
    }
}
