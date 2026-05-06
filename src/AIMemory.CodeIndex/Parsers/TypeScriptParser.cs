using System.Text.RegularExpressions;

namespace AIMemory.CodeIndex.Parsers;

public partial class TypeScriptParser : ILanguageParser
{
    public string Language => "typescript";
    public string[] FileExtensions => [".ts", ".tsx", ".js", ".jsx"];

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s+(\w+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)(?:\s*<[^>]*>)?(?:\s+extends\s+(\w+))?(?:\s+implements\s+([^{]+))?\s*\{", RegexOptions.Multiline)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?interface\s+(\w+)(?:\s*<[^>]*>)?(?:\s+extends\s+([^{]+))?\s*\{", RegexOptions.Multiline)]
    private static partial Regex InterfaceRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?type\s+(\w+)(?:\s*<[^>]*>)?\s*=", RegexOptions.Multiline)]
    private static partial Regex TypeAliasRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::\s*[^=]+)?\s*=\s*(?:async\s+)?\(?", RegexOptions.Multiline)]
    private static partial Regex ArrowFunctionRegex();

    [GeneratedRegex(@"^\s*(?:public|private|protected|static|async|readonly|\s)*(\w+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex MethodRegex();

    public List<ParsedSymbol> Parse(string filePath, string content)
    {
        var symbols = new List<ParsedSymbol>();
        var lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var classMatch = ClassRegex().Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                var endLine = FindBraceBlockEnd(lines, i);
                symbols.Add(CreateSymbol(name, name, "class", line.Trim(), i, endLine, content, null));
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

            var typeMatch = TypeAliasRegex().Match(line);
            if (typeMatch.Success)
            {
                var name = typeMatch.Groups[1].Value;
                symbols.Add(CreateSymbol(name, name, "type", line.Trim(), i, i + 1, content, null));
                continue;
            }

            var funcMatch = FunctionRegex().Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                var endLine = FindBraceBlockEnd(lines, i);
                symbols.Add(CreateSymbol(name, name, "function", line.Trim(), i, endLine, content, null));
                continue;
            }

            var arrowMatch = ArrowFunctionRegex().Match(line);
            if (arrowMatch.Success)
            {
                var name = arrowMatch.Groups[1].Value;
                // Skip common non-function patterns
                if (name is "if" or "for" or "while" or "switch" or "catch" or "return") continue;
                var endLine = FindBraceBlockEnd(lines, i);
                symbols.Add(CreateSymbol(name, name, "function", line.Trim(), i, endLine, content, null));
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
