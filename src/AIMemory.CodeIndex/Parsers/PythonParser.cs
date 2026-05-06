using System.Text.RegularExpressions;

namespace AIMemory.CodeIndex.Parsers;

public partial class PythonParser : ILanguageParser
{
    public string Language => "python";
    public string[] FileExtensions => [".py"];

    [GeneratedRegex(@"^(\s*)(?:async\s+)?def\s+(\w+)\s*\(([^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^(\s*)class\s+(\w+)(?:\s*\(([^)]*)\))?\s*:", RegexOptions.Multiline)]
    private static partial Regex ClassRegex();

    public List<ParsedSymbol> Parse(string filePath, string content)
    {
        var symbols = new List<ParsedSymbol>();
        var lines = content.Split('\n');

        // Track class context by indentation level
        var classStack = new Stack<(string Name, int Indent)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.Length - line.TrimStart().Length;

            // Pop classes that are no longer in scope
            while (classStack.Count > 0 && classStack.Peek().Indent >= indent)
                classStack.Pop();

            var classMatch = ClassRegex().Match(line);
            if (classMatch.Success)
            {
                var name = classMatch.Groups[2].Value;
                var bases = classMatch.Groups[3].Value;
                var endLine = FindBlockEnd(lines, i, indent);
                var parentClass = classStack.Count > 0 ? classStack.Peek().Name : null;
                var qualified = parentClass != null ? $"{parentClass}.{name}" : name;

                symbols.Add(CreateSymbol(name, qualified, "class",
                    $"class {name}" + (string.IsNullOrEmpty(bases) ? "" : $"({bases})"),
                    i, endLine, content, parentClass));

                classStack.Push((qualified, indent));
                continue;
            }

            var funcMatch = FunctionRegex().Match(line);
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[2].Value;
                var @params = funcMatch.Groups[3].Value;
                var endLine = FindBlockEnd(lines, i, indent);
                var parentClass = classStack.Count > 0 ? classStack.Peek().Name : null;
                var kind = parentClass != null ? "method" : "function";
                var qualified = parentClass != null ? $"{parentClass}.{name}" : name;

                symbols.Add(CreateSymbol(name, qualified, kind,
                    $"def {name}({@params})", i, endLine, content, parentClass));
            }
        }

        return symbols;
    }

    private static int FindBlockEnd(string[] lines, int startLine, int baseIndent)
    {
        for (int i = startLine + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (indent <= baseIndent)
                return i; // exclusive
        }
        return lines.Length;
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
