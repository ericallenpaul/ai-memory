namespace AIMemory.Models.Dtos;

public class IndexFolderRequest
{
    public string FolderPath { get; set; } = string.Empty;
    public string? Name { get; set; }
}

public class IndexGitHubRequest
{
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string? Branch { get; set; }
}

public class CodeRepoResponse
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class FileTreeNode
{
    public string Path { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int SymbolCount { get; set; }
}

public class FileTreeResponse
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<FileTreeNode> Files { get; set; } = [];
}

public class SymbolOutline
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public string? Summary { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? ParentSymbolKey { get; set; }
}

public class FileOutlineResponse
{
    public string ProjectId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public List<SymbolOutline> Symbols { get; set; } = [];
}

public class SymbolResponse
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public string? Summary { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string SourceCode { get; set; } = string.Empty;
}

public class SymbolMatch
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string QualifiedName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Signature { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
}

public class TextMatch
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string LineContent { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
}

public class RepoOutlineResponse
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public List<LanguageSummary> Languages { get; set; } = [];
    public List<string> TopLevelDirectories { get; set; } = [];
    public List<SymbolOutline> KeySymbols { get; set; } = [];
}

public class LanguageSummary
{
    public string Language { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
}
