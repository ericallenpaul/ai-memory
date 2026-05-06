using System.Text.RegularExpressions;

namespace AIMemory.CodeIndex.Parsers;

public partial class GoParser : ILanguageParser
{
    public string Language => "go";
    public string[] FileExtensions => [".go"];

    [GeneratedRegex(@"^func\s+(?:\((\w+)\s+\*?(\w+)\)\s+)?(\w+)\s*\(([^)]*)\)(?:\s*(?:\(([^)]*)\)|(\w+(?:\.\w+)?)))?\s*\{", RegexOptions.Multiline)]
    private static partial Regex FuncRegex();

    [GeneratedRegex(@"^type\s+(\w+)\s+struct\s*\{", RegexOptions.Multiline)]
    private static partial Regex StructRegex();

    [GeneratedRegex(@"^type\s+(\w+)\s+interface\s*\{", RegexOptions.Multiline)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"^type\s+(\w+)\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex TypeAliasRegex();

    public List<ParsedSymbol> Parse(string filePath, string content)
    {
        var symbols = new List<ParsedSymbol>();
        var lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var structMatch = StructRegex().Match(line);
            if (structMatch.Success)
            {
                var name = structMatch.Groups[1].Value;
                var endLine = FindBraceBlockEnd(lines, i);
                symbols.Add(CreateSymbol(name, name, "struct", line.Trim(), i, endLine, content, null));
                continue;
            }

            var ifaceMatch = InterfaceRegex().Match(line);
            if (ifaceMatch.Success)
            {
                var name = ifaceMatch.Groups[1].Value;
                var endLine = FindBraceBlockEnd(lines, i);
                symbols.Add(CreateSymbol(name, name, "interface", line.Trim(), i, endLine, content, null));
                continue;
            }

            var funcMatch = FuncRegex().Match(line);
            if (funcMatch.Success)
            {
                var receiverType = funcMatch.Groups[2].Value;
                var funcName = funcMatch.Groups[3].Value;
                var kind = string.IsNullOrEmpty(receiverType) ? "function" : "method";
                var qualified = string.IsNullOrEmpty(receiverType) ? funcName : $"{receiverType}.{funcName}";
                var endLine = FindBraceBlockEnd(lines, i);
                var parent = string.IsNullOrEmpty(receiverType) ? null : receiverType;

                symbols.Add(CreateSymbol(funcName, qualified, kind, line.Trim(), i, endLine, content, parent));
                continue;
            }

            var typeMatch = TypeAliasRegex().Match(line);
            if (typeMatch.Success && !line.Contains("struct") && !line.Contains("interface"))
            {
                var name = typeMatch.Groups[1].Value;
                symbols.Add(CreateSymbol(name, name, "type", line.Trim(), i, i + 1, content, null));
            }
        }

        return symbols;
    }

    private static int FindBraceBlockEnd(string[] lines, int startLine)
    {
        int braceCount = 0;
        bool foundOpen = false;

        for (int i = startLine; i < lines.Length; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { braceCount++; foundOpen = true; }
                else if (ch == '}') braceCount--;
            }

            if (foundOpen && braceCount <= 0)
                return i + 1;
        }

        return Math.Min(startLine + 1, lines.Length);
    }

    private static ParsedSymbol CreateSymbol(string name, string qualifiedName, string kind,
        string signature, int startLine, int endLine, string content, string? parentName)
    {
        var byteStart = GetByteOffset(content, startLine);
        var byteEnd = GetByteOffset(content, endLine);

        return new ParsedSymbol
        {
            Name = name,
            QualifiedName = qualifiedName,
            Kind = kind,
            Signature = signature,
            StartLine = startLine + 1,
            EndLine = endLine,
            StartByte = byteStart,
            EndByte = byteEnd,
            ParentName = parentName
        };
    }

    private static long GetByteOffset(string content, int lineIndex)
    {
        int offset = 0;
        int currentLine = 0;
        for (int i = 0; i < content.Length && currentLine < lineIndex; i++)
        {
            if (content[i] == '\n') currentLine++;
            offset = i + 1;
        }
        return offset;
    }
}
