using NLog;
using NLog.Extensions.Logging;
using AIMemory.Ingestor;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Redaction;
using AIMemory.Ingestor.Transport;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;

var builder = Host.CreateApplicationBuilder(args);

// Load config from ProgramData (written by Config App) with reload support
var programDataConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "AIMemory", "Ingestor", "appsettings.json");
builder.Configuration.AddJsonFile(programDataConfigPath, optional: true, reloadOnChange: true);

// Parse CLI args
var runOnce = args.Contains("--once");
var testRedaction = args.Contains("--test-redaction");
var showStatus = args.Contains("--status");

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["RunOnce"] = runOnce.ToString()
});

// NLog
builder.Logging.ClearProviders();
builder.Logging.AddNLog(new NLogProviderOptions { ShutdownOnDispose = true });

// Configuration
builder.Services.Configure<IngestorConfig>(builder.Configuration.GetSection("Ingestor"));

// Load config for manual setup
var config = new IngestorConfig();
builder.Configuration.GetSection("Ingestor").Bind(config);

// Override from env
config.ApiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY") ?? config.ApiKey;
var apiUrl = Environment.GetEnvironmentVariable("AIMEMORY_API_URL") ?? config.AIMemoryApiBaseUrl;

// Auto-discover API port from port file if URL is not configured
if (string.IsNullOrWhiteSpace(apiUrl))
{
    var portFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "AIMemory", "Api", "port");
    if (File.Exists(portFilePath)
        && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var discoveredPort)
        && discoveredPort > 0)
    {
        apiUrl = $"http://localhost:{discoveredPort}";
    }
}

// Checkpoint store
var checkpointPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AIMemory", "Ingestor", "checkpoints.json");

var startupLogger = NLog.LogManager.GetCurrentClassLogger();
if (string.IsNullOrWhiteSpace(apiUrl))
    startupLogger.Warn("No API URL configured — batches will fail until API URL is set");
else
    startupLogger.Info("Resolved API URL: {ApiUrl}", apiUrl);
startupLogger.Info("Checkpoint store: {Path}", checkpointPath);
startupLogger.Info("Sources enabled: {Sources}",
    string.Join(", ", config.Sources.Where(s => s.Enabled).Select(s => $"{s.Name} ({s.Segment})")));

builder.Services.AddSingleton<ICheckpointStore>(sp =>
    new JsonCheckpointStore(checkpointPath, sp.GetRequiredService<ILogger<JsonCheckpointStore>>()));

// Redaction
builder.Services.AddSingleton<IRedactionPipeline>(sp =>
    new RedactionPipeline(config.Redaction, sp.GetRequiredService<ILogger<RedactionPipeline>>()));

// Source adapters
builder.Services.AddSingleton<ISourceAdapter, ClaudeCodeAdapter>();
builder.Services.AddSingleton<ISourceAdapter, CodexCliAdapter>();

// Code index adapter + dependencies
builder.Services.AddSingleton<FileFilter>();
builder.Services.AddSingleton<ILanguageParser, CSharpParser>();
builder.Services.AddSingleton<ILanguageParser, PythonParser>();
builder.Services.AddSingleton<ILanguageParser, TypeScriptParser>();
builder.Services.AddSingleton<ILanguageParser, GoParser>();
builder.Services.AddSingleton<ParserRegistry>(sp =>
    new ParserRegistry(sp.GetServices<ILanguageParser>()));
builder.Services.AddSingleton<ISourceAdapter, CodeAdapter>();

// HTTP Client with Polly retry
builder.Services.AddHttpClient<IAIMemoryClient, AIMemoryHttpClient>(client =>
{
    client.BaseAddress = new Uri(apiUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrEmpty(config.ApiKey))
        client.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
});

// Outbox
var outboxPath = !string.IsNullOrEmpty(config.Outbox.Path) ? config.Outbox.Path
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIMemory", "Ingestor", "outbox");

builder.Services.AddSingleton(sp =>
    new OutboxStore(outboxPath, config.Outbox.MaxSizeMb, sp.GetRequiredService<ILogger<OutboxStore>>()));

// Cross-platform service hosting. Both calls are safe no-ops when not running under SCM/systemd.
builder.Services.AddWindowsService(o => o.ServiceName = "aimemory-ingestor");
builder.Services.AddSystemd();

// Handle special CLI modes before starting worker
if (showStatus)
{
    var store = new JsonCheckpointStore(checkpointPath,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger<JsonCheckpointStore>());
    Console.WriteLine("Checkpoint Status:");
    foreach (var (path, cp) in store.GetAll())
    {
        Console.WriteLine($"  {path}");
        Console.WriteLine($"    Offset: {cp.LastProcessedOffset}, Size: {cp.FileSize}, LastSeen: {cp.LastSeenAt}");
    }
    return;
}

if (testRedaction)
{
    Console.WriteLine("Redaction Test Mode — scanning sources (no upload)");
    var redaction = new RedactionPipeline(config.Redaction,
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger<RedactionPipeline>());
    var adapters = new ISourceAdapter[]
    {
        new ClaudeCodeAdapter(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ClaudeCodeAdapter>()),
        new CodexCliAdapter(LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CodexCliAdapter>())
    };

    foreach (var source in config.Sources.Where(s => s.Enabled))
    {
        var adapter = adapters.FirstOrDefault(a => a.SourceName == source.Name);
        if (adapter == null) continue;

        foreach (var file in adapter.DiscoverFiles(source))
        {
            if (redaction.ShouldExcludeFile(file))
            {
                Console.WriteLine($"  EXCLUDED: {file}");
                continue;
            }

            foreach (var record in adapter.ReadNewRecords(file, null).Take(10))
            {
                var original = record.Line;
                var redacted = redaction.Redact(original);
                if (original != redacted)
                    Console.WriteLine($"  REDACTED in {file}:{record.Offset}");
            }
        }
    }
    return;
}

// Worker service
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
