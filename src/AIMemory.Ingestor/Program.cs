using NLog;
using NLog.Extensions.Logging;
using AIMemory.Identity;
using AIMemory.Ingestor;
using AIMemory.Ingestor.Adapters;
using AIMemory.Ingestor.Checkpointing;
using AIMemory.Ingestor.Configuration;
using AIMemory.Ingestor.Hashing;
using AIMemory.Ingestor.Redaction;
using AIMemory.Ingestor.Transport;
using AIMemory.CodeIndex.Git;
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
var printHostId = args.Contains("--print-host-id");

// --print-host-id is consumed by the desktop pairing wizard (phase 9). It needs the
// machine's stable host_id BEFORE the ingestor service is configured (the wizard issues
// POST /api/pairings on the user's behalf, which requires the host_id in the body). Doing
// it inline here keeps HostIdProvider as the single source of truth — the wizard doesn't
// need to re-implement the algorithm in Rust. We resolve the salt directory the same way
// the rest of startup does and exit immediately, before any DI/host wiring runs.
if (printHostId)
{
    var saltDirEarly = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AIMemory", "Ingestor")
        : InstallSaltStore.GetDefaultDirectory();
    Directory.CreateDirectory(saltDirEarly);
    var earlySaltStore = new InstallSaltStore(saltDirEarly);
    var earlyProvider = new HostIdProvider(earlySaltStore);
    Console.WriteLine(earlyProvider.GetHostId());
    return;
}

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

// Override from env (local mode only — remote uses Ingestor.Remote.* settings)
config.ApiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY") ?? config.ApiKey;
var apiUrl = Environment.GetEnvironmentVariable("AIMEMORY_API_URL") ?? config.AIMemoryApiBaseUrl;

// Validate the resolved configuration. Remote mode without all three remote fields is a
// fail-fast at startup — better than silently misbehaving on the first batch.
try
{
    IngestorConfigValidator.Validate(config);
}
catch (IngestorConfigurationException ex)
{
    Console.Error.WriteLine($"FATAL: ingestor configuration is invalid: {ex.Message}");
    NLog.LogManager.Shutdown();
    Environment.Exit(78); // EX_CONFIG (sysexits)
    return;
}

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
builder.Services.AddSingleton<GitChangeDetector>();
builder.Services.AddSingleton<ISourceAdapter, CodeAdapter>();

// Streaming SHA-256 hasher — used by CodeAdapter for content_sha256 derivation.
builder.Services.AddSingleton<IFileHasher, StreamingFileHasher>();

// Identity (host_id + project_id). Single-machine installs share the salt with the API so
// both services compute the same host_id. We prefer the API's directory when its salt file
// is present; otherwise we fall back to the ingestor-local directory (the remote-only /
// secondary-host shape, where the API service isn't installed on this machine).
string saltDir;
if (OperatingSystem.IsWindows())
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var apiSaltDir = Path.Combine(programData, "AIMemory", "Api");
    var ingestorSaltDir = Path.Combine(programData, "AIMemory", "Ingestor");
    saltDir = File.Exists(Path.Combine(apiSaltDir, "install-salt.bin"))
        ? apiSaltDir
        : ingestorSaltDir;
}
else
{
    saltDir = InstallSaltStore.GetDefaultDirectory();
}
Directory.CreateDirectory(saltDir);
builder.Services.AddSingleton<IInstallSaltStore>(_ => new InstallSaltStore(saltDir));
builder.Services.AddSingleton<IHostIdProvider, HostIdProvider>();
builder.Services.AddSingleton<IProjectIdResolver, ProjectIdResolver>();
builder.Services.AddSingleton<IIngestorContext, IngestorContext>();

// Sink wiring. Local mode keeps the existing HTTP-loopback behavior unchanged; remote mode
// uses the fingerprint-pinned HTTPS client.
if (config.Mode == IngestorMode.Remote)
{
    startupLogger.Info("Ingestor mode: REMOTE (endpoint={Endpoint})", config.Remote.Endpoint);

    var remoteClient = RemoteSinkHttpClientBuilder.Build(config.Remote);
    builder.Services.AddSingleton(remoteClient);
    builder.Services.AddSingleton<ILedgerSink>(sp =>
        new RemoteSink(remoteClient, sp.GetService<ILogger<RemoteSink>>()));
}
else
{
    startupLogger.Info("Ingestor mode: LOCAL (loopback to {Url})", apiUrl);

    builder.Services.AddHttpClient<IAIMemoryClient, AIMemoryHttpClient>(client =>
    {
        // Tolerate an empty apiUrl at registration time — preserves the pre-7b behavior
        // where the ingestor starts up and warns even if the API URL hasn't been resolved
        // yet. Calls will fail at send time, which the LocalSink translates to a transient
        // failure and the worker queues to the outbox.
        if (!string.IsNullOrWhiteSpace(apiUrl))
            client.BaseAddress = new Uri(apiUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            // Phase 7a moved the API to the new header name. Local-loopback still gets the
            // bypass when no key is sent, but if a key is configured we send it under the new
            // header so the auth path actually validates the key.
            client.DefaultRequestHeaders.Add(RemoteSink.ApiKeyHeaderName, config.ApiKey);
        }
    });

    builder.Services.AddSingleton<ILedgerSink, LocalSink>();
}

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
