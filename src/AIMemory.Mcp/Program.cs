using ModelContextProtocol;

// Auto-discover API port from port file if env var not set
var apiBaseUrl = Environment.GetEnvironmentVariable("AIMEMORY_API_URL") ?? "";
if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    var portFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AIMemory", "Api", "port");
    if (File.Exists(portFilePath)
        && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var port)
        && port > 0)
    {
        apiBaseUrl = $"http://localhost:{port}";
    }
}

var apiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY") ?? "";

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

Action<HttpClient> configureClient = client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
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
