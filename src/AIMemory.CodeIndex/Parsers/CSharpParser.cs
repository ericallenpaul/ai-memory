using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AIMemory.CodeIndex.Parsers;

public class CSharpParser : ILanguageParser
{
    public string Language => "csharp";
    public string[] FileExtensions => [".cs"];

    public List<ParsedSymbol> Parse(string filePath, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        var root = tree.GetRoot();
        var symbols = new List<ParsedSymbol>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case NamespaceDeclarationSyntax ns:
                    AddSymbol(symbols, ns.Name.ToString(), ns.Name.ToString(), "namespace",
                        $"namespace {ns.Name}", node, content, null);
                    break;

                case FileScopedNamespaceDeclarationSyntax ns:
                    AddSymbol(symbols, ns.Name.ToString(), ns.Name.ToString(), "namespace",
                        $"namespace {ns.Name}", node, content, null);
                    break;

                case ClassDeclarationSyntax cls:
                    var clsQualified = GetQualifiedName(cls);
                    AddSymbol(symbols, cls.Identifier.Text, clsQualified, "class",
                        GetTypeSignature(cls), node, content, GetParentTypeName(cls));
                    break;

                case RecordDeclarationSyntax rec:
                    var recQualified = GetQualifiedName(rec);
                    AddSymbol(symbols, rec.Identifier.Text, recQualified, "record",
                        GetRecordSignature(rec), node, content, GetParentTypeName(rec));
                    break;

                case StructDeclarationSyntax str:
                    var strQualified = GetQualifiedName(str);
                    AddSymbol(symbols, str.Identifier.Text, strQualified, "struct",
                        GetTypeSignature(str), node, content, GetParentTypeName(str));
                    break;

                case InterfaceDeclarationSyntax iface:
                    var ifaceQualified = GetQualifiedName(iface);
                    AddSymbol(symbols, iface.Identifier.Text, ifaceQualified, "interface",
                        GetTypeSignature(iface), node, content, GetParentTypeName(iface));
                    break;

                case EnumDeclarationSyntax enm:
                    var enmQualified = GetQualifiedName(enm);
                    AddSymbol(symbols, enm.Identifier.Text, enmQualified, "enum",
                        $"enum {enm.Identifier.Text}", node, content, GetParentTypeName(enm));
                    break;

                case MethodDeclarationSyntax method:
                    var methodParent = GetParentTypeName(method);
                    var methodQualified = methodParent != null
                        ? $"{methodParent}.{method.Identifier.Text}"
                        : method.Identifier.Text;
                    AddSymbol(symbols, method.Identifier.Text, methodQualified, "method",
                        GetMethodSignature(method), node, content, methodParent);
                    break;

                case ConstructorDeclarationSyntax ctor:
                    var ctorParent = GetParentTypeName(ctor);
                    var ctorQualified = ctorParent != null
                        ? $"{ctorParent}.{ctor.Identifier.Text}"
                        : ctor.Identifier.Text;
                    AddSymbol(symbols, ctor.Identifier.Text, ctorQualified, "constructor",
                        GetConstructorSignature(ctor), node, content, ctorParent);
                    break;

                case PropertyDeclarationSyntax prop:
                    var propParent = GetParentTypeName(prop);
                    var propQualified = propParent != null
                        ? $"{propParent}.{prop.Identifier.Text}"
                        : prop.Identifier.Text;
                    AddSymbol(symbols, prop.Identifier.Text, propQualified, "property",
                        $"{prop.Type} {prop.Identifier.Text}", node, content, propParent);
                    break;

                case DelegateDeclarationSyntax del:
                    var delQualified = GetQualifiedName(del);
                    AddSymbol(symbols, del.Identifier.Text, delQualified, "delegate",
                        $"delegate {del.ReturnType} {del.Identifier.Text}{del.ParameterList}",
                        node, content, GetParentTypeName(del));
                    break;
            }
        }

        return symbols;
    }

    private static void AddSymbol(List<ParsedSymbol> symbols, string name, string qualifiedName,
        string kind, string? signature, SyntaxNode node, string content, string? parentName)
    {
        var lineSpan = node.GetLocation().GetLineSpan();

        symbols.Add(new ParsedSymbol
        {
            Name = name,
            QualifiedName = qualifiedName,
            Kind = kind,
            Signature = signature,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            StartByte = node.SpanStart,
            EndByte = node.Span.End,
            ParentName = parentName
        });
    }

    private static string GetQualifiedName(MemberDeclarationSyntax member)
    {
        var parts = new List<string>();

        if (member is BaseTypeDeclarationSyntax type)
            parts.Add(type.Identifier.Text);
        else if (member is DelegateDeclarationSyntax del)
            parts.Add(del.Identifier.Text);

        var parent = member.Parent;
        while (parent != null)
        {
            if (parent is BaseTypeDeclarationSyntax parentType)
                parts.Add(parentType.Identifier.Text);
            else if (parent is BaseNamespaceDeclarationSyntax ns)
                parts.Add(ns.Name.ToString());
            parent = parent.Parent;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string? GetParentTypeName(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            if (parent is BaseTypeDeclarationSyntax type)
                return GetQualifiedName(type);
            if (parent is BaseNamespaceDeclarationSyntax)
                return null;
            parent = parent.Parent;
        }
        return null;
    }

    private static string GetTypeSignature(TypeDeclarationSyntax type)
    {
        var modifiers = type.Modifiers.ToString();
        var keyword = type switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            _ => "type"
        };
        var bases = type.BaseList != null ? $" {type.BaseList}" : "";
        return $"{modifiers} {keyword} {type.Identifier.Text}{type.TypeParameterList}{bases}".Trim();
    }

    private static string GetRecordSignature(RecordDeclarationSyntax rec)
    {
        var modifiers = rec.Modifiers.ToString();
        var keyword = rec.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record";
        var parameters = rec.ParameterList?.ToString() ?? "";
        var bases = rec.BaseList != null ? $" {rec.BaseList}" : "";
        return $"{modifiers} {keyword} {rec.Identifier.Text}{rec.TypeParameterList}{parameters}{bases}".Trim();
    }

    private static string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.ToString();
        return $"{modifiers} {method.ReturnType} {method.Identifier.Text}{method.TypeParameterList}{method.ParameterList}".Trim();
    }

    private static string GetConstructorSignature(ConstructorDeclarationSyntax ctor)
    {
        var modifiers = ctor.Modifiers.ToString();
        return $"{modifiers} {ctor.Identifier.Text}{ctor.ParameterList}".Trim();
    }
}
