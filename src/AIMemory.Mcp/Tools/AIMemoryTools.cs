using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Server;
using AIMemory.Models.Dtos;

namespace AIMemory.Mcp.Tools;

[McpServerToolType]
public class AIMemoryTools
{
    private readonly HttpClient _http;

    public AIMemoryTools(HttpClient http)
    {
        _http = http;
    }

    [McpServerTool, Description("Create a new session in AIMemory to track an AI conversation or workflow.")]
    public async Task<string> CreateSession(
        [Description("Title for the session")] string title,
        [Description("Project name")] string? project = null,
        [Description("Repository name")] string? repo = null,
        [Description("Git branch")] string? branch = null,
        [Description("Comma-separated tags")] string? tags = null)
    {
        var request = new CreateSessionRequest
        {
            Title = title,
            Project = project,
            Repo = repo,
            Branch = branch,
            Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        var response = await _http.PostAsJsonAsync("/sessions", request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return body;
    }

    [McpServerTool, Description("Append a message to an existing AIMemory session.")]
    public async Task<string> AppendMessage(
        [Description("Session ID (UUID)")] string sessionId,
        [Description("Message role: system, user, assistant, or tool")] string role,
        [Description("Message content")] string content,
        [Description("AI provider name")] string? provider = null,
        [Description("Model name")] string? model = null)
    {
        var request = new AppendMessageRequest
        {
            Role = role,
            Content = content,
            Provider = provider,
            Model = model
        };

        var response = await _http.PostAsJsonAsync($"/sessions/{sessionId}/messages", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Record a tool call in an AIMemory session.")]
    public async Task<string> AppendToolCall(
        [Description("Session ID (UUID)")] string sessionId,
        [Description("Name of the tool that was called")] string toolName,
        [Description("Tool arguments as JSON string")] string? argumentsJson = null,
        [Description("Tool result as JSON string")] string? resultJson = null)
    {
        var request = new AppendToolCallRequest
        {
            ToolName = toolName,
            ArgumentsJson = argumentsJson != null ? JsonSerializer.Deserialize<JsonElement>(argumentsJson) : null,
            ResultJson = resultJson != null ? JsonSerializer.Deserialize<JsonElement>(resultJson) : null
        };

        var response = await _http.PostAsJsonAsync($"/sessions/{sessionId}/toolcalls", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Search AIMemory message history using full-text search. Returns relevant snippets from past AI conversations.")]
    public async Task<string> Search(
        [Description("Search query")] string query,
        [Description("Filter by project name")] string? project = null,
        [Description("Maximum results to return")] int limit = 10)
    {
        var url = $"/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        if (!string.IsNullOrEmpty(project))
            url += $"&project={Uri.EscapeDataString(project)}";

        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [McpServerTool, Description("Retrieve an AIMemory session with its message history.")]
    public async Task<string> GetSession(
        [Description("Session ID (UUID)")] string sessionId,
        [Description("Maximum messages to return")] int limit = 100)
    {
        var response = await _http.GetAsync($"/sessions/{sessionId}?limit={limit}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
