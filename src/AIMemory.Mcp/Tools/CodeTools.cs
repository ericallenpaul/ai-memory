using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using AIMemory.Models.Dtos;

namespace AIMemory.Mcp.Tools;

[McpServerToolType]
public class CodeTools
{
    private readonly HttpClient _http;

    public CodeTools(HttpClient http)
    {
        _http = http;
    }

    [McpServerTool, Description("Index a local code folder for structured symbol retrieval. Returns repository info with file and symbol counts.")]
    public async Task<string> IndexFolder(
        [Description("Absolute path to the folder to index")] string folderPath,
        [Description("Optional name for the repository")] string? name = null)
    {
        var request = new IndexFolderRequest { FolderPath = folderPath, Name = name };
        var response = await _http.PostAsJsonAsync("/api/code/index-folder", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("List all indexed code repositories.")]
    public async Task<string> ListCodeRepos()
    {
        var response = await _http.GetAsync("/api/code/repos");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Get the file tree of an indexed repository. Returns all files with their languages and sizes.")]
    public async Task<string> GetFileTree(
        [Description("Repository name or ID")] string repo)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var response = await _http.GetAsync($"/api/code/repos/{repoId}/tree");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Get the symbol outline for a file in an indexed repo. Shows all functions, classes, methods with their signatures.")]
    public async Task<string> GetFileOutline(
        [Description("Repository name or ID")] string repo,
        [Description("Relative file path within the repository")] string filePath)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var response = await _http.GetAsync($"/api/code/repos/{repoId}/outline?file={Uri.EscapeDataString(filePath)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Get the full source code of a specific symbol by its key (e.g. 'src/Main.cs::MyClass.MyMethod#method').")]
    public async Task<string> GetSymbol(
        [Description("Repository name or ID")] string repo,
        [Description("Symbol key in format: filepath::QualifiedName#kind")] string symbolKey)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var response = await _http.GetAsync($"/api/code/repos/{repoId}/symbol?key={Uri.EscapeDataString(symbolKey)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Batch retrieve multiple symbols by their keys. More efficient than individual GetSymbol calls.")]
    public async Task<string> GetSymbols(
        [Description("Repository name or ID")] string repo,
        [Description("JSON array of symbol keys")] string symbolKeysJson)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var keys = JsonSerializer.Deserialize<List<string>>(symbolKeysJson) ?? [];
        var response = await _http.PostAsJsonAsync($"/api/code/repos/{repoId}/symbols", keys);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Search for code symbols (functions, classes, methods) by name. Returns matching symbols with their signatures and locations.")]
    public async Task<string> SearchSymbols(
        [Description("Search query (matches symbol name or qualified name)")] string query,
        [Description("Optional repository name or ID to search within")] string? repo = null,
        [Description("Optional kind filter: function, class, method, interface, struct, property, enum")] string? kind = null,
        [Description("Maximum results to return")] int limit = 20)
    {
        var url = $"/api/code/search/symbols?q={Uri.EscapeDataString(query)}&limit={limit}";

        if (!string.IsNullOrEmpty(repo))
        {
            var repoId = await ResolveRepoIdAsync(repo);
            if (repoId != null) url += $"&repo={repoId}";
        }

        if (!string.IsNullOrEmpty(kind))
            url += $"&kind={Uri.EscapeDataString(kind)}";

        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Full-text search within indexed code files. Returns matching lines with file paths and line numbers.")]
    public async Task<string> SearchCodeText(
        [Description("Text to search for")] string query,
        [Description("Optional repository name or ID to search within")] string? repo = null,
        [Description("Optional file path to search within")] string? filePath = null,
        [Description("Maximum results to return")] int limit = 20)
    {
        var url = $"/api/code/search/text?q={Uri.EscapeDataString(query)}&limit={limit}";

        if (!string.IsNullOrEmpty(repo))
        {
            var repoId = await ResolveRepoIdAsync(repo);
            if (repoId != null) url += $"&repo={repoId}";
        }

        if (!string.IsNullOrEmpty(filePath))
            url += $"&file={Uri.EscapeDataString(filePath)}";

        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Get a high-level overview of an indexed repository including languages, directory structure, and key symbols.")]
    public async Task<string> GetRepoOutline(
        [Description("Repository name or ID")] string repo)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var response = await _http.GetAsync($"/api/code/repos/{repoId}/outline");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Remove an indexed code repository and all its indexed data.")]
    public async Task<string> RemoveCodeRepo(
        [Description("Repository name or ID")] string repo)
    {
        var repoId = await ResolveRepoIdAsync(repo);
        if (repoId == null) return JsonSerializer.Serialize(new { error = "Repository not found" });

        var response = await _http.DeleteAsync($"/api/code/repos/{repoId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string?> ResolveRepoIdAsync(string repoNameOrId)
    {
        // Try as GUID first
        if (Guid.TryParse(repoNameOrId, out _))
            return repoNameOrId;

        // Look up by name
        var response = await _http.GetAsync("/api/code/repos");
        if (!response.IsSuccessStatusCode) return null;

        var repos = await response.Content.ReadFromJsonAsync<List<CodeRepoResponse>>();
        var match = repos?.FirstOrDefault(r =>
            r.Name.Equals(repoNameOrId, StringComparison.OrdinalIgnoreCase));

        return match?.RepositoryId.ToString();
    }
}
