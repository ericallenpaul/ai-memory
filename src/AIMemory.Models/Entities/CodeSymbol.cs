namespace AIMemory.Models.Entities;

/// <summary>
/// A symbol parsed out of a content blob. Owned by content (<see cref="ContentSha256"/>),
/// not by file path — so the same symbol on two hosts dedupes naturally.
///
/// <para><see cref="SymbolKey"/> format: <c>project_id::QualifiedName#kind</c>. The format
/// changed in phase 6 from <c>filepath::QualifiedName#kind</c>; the symbol_key is an opaque
/// token to MCP callers so this is internal-only.</para>
/// </summary>
public class CodeSymbol
{
    public Guid SymbolId { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
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

}
