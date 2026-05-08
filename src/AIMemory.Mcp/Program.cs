using System.Text.Json;
using ModelContextProtocol;

// Discovery order:
//   1. Explicit env vars (highest priority — non-desktop deployments override here)
//   2. runtime.json written by the API service on startup (desktop bundle path)
//   3. Legacy port file (back-compat with pre-Phase-4 deployments)
var apiBaseUrl = Environment.GetEnvironmentVariable("AIMEMORY_API_URL") ?? "";
var apiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY") ?? "";

if (string.IsNullOrWhiteSpace(apiBaseUrl) || string.IsNullOrWhiteSpace(apiKey))
{
    var apiDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AIMemory", "Api");
    var runtimeJsonPath = Path.Combine(apiDir, "runtime.json");

    if (File.Exists(runtimeJsonPath))
    {
        try
        {
            using var stream = File.OpenRead(runtimeJsonPath);
            var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (string.IsNullOrWhiteSpace(apiBaseUrl)
                && root.TryGetProperty("baseUrl", out var baseUrlElem))
            {
                apiBaseUrl = baseUrlElem.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(apiKey)
                && root.TryGetProperty("apiKey", out var apiKeyElem))
            {
                apiKey = apiKeyElem.GetString() ?? "";
            }
        }
        catch
        {
            // Fall through to port-file fallback if runtime.json is malformed.
        }
    }

    if (string.IsNullOrWhiteSpace(apiBaseUrl))
    {
        var portFilePath = Path.Combine(apiDir, "port");
        if (File.Exists(portFilePath)
            && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var port)
            && port > 0)
        {
            apiBaseUrl = $"http://localhost:{port}";
        }
    }
}

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

Action<HttpClient> configureClient = client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("X-AIMemory-Api-Key", apiKey);
};

builder.Services.AddHttpClient<AIMemory.Mcp.Tools.AIMemoryTools>(configureClient);
builder.Services.AddHttpClient<AIMemory.Mcp.Tools.CodeTools>(client =>
{
    configureClient(client);
    // Code indexing can take longer for large repos
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
