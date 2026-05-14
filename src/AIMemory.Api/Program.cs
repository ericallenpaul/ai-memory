using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using NLog.Web;
using AIMemory.Api.Distributed;
using AIMemory.Api.Middleware;
using AIMemory.Api.Tls;
using AIMemory.Data;
using AIMemory.Data.Repositories;
using AIMemory.Models.Dtos;
using AIMemory.Models.Entities;
using AIMemory.Models.Events;
using AIMemory.CodeIndex;
using AIMemory.CodeIndex.Parsers;
using AIMemory.CodeIndex.Security;
using AIMemory.Data.Migrations;
using AIMemory.Identity;

var builder = WebApplication.CreateBuilder(args);

// Cross-platform service hosting. UseWindowsService is a no-op when not running under SCM,
// UseSystemd is a no-op when not running under systemd, so dev runs unaffected.
builder.Host.UseWindowsService(o => o.ServiceName = "aimemory-api");
builder.Host.UseSystemd();

// Load config from ProgramData (written by installer or first-run) with reload support
var programDataConfigDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "AIMemory", "Api");
var programDataConfigPath = Path.Combine(programDataConfigDir, "appsettings.json");
builder.Configuration.AddJsonFile(programDataConfigPath, optional: true, reloadOnChange: true);

// Determine listen port: configured > persisted > random
// In Development, let launchSettings.json control the port (for SpaProxy + Vite to work)
var isDevelopment = builder.Environment.IsDevelopment();
var configuredPort = builder.Configuration.GetValue<int>("AIMemory:Port");
var portFilePath = Path.Combine(programDataConfigDir, "port");

// Distributed-ingestion listener config. Persisted to distributed.json so the Allow-remote
// toggle and TLS state survive restarts. Phase 7a — see docs/distributed-ingestion-design.md §3.6.
var distributedStore = new DistributedConfigStore(programDataConfigDir);
var distributedConfig = distributedStore.Load();

if (!isDevelopment)
{
    if (configuredPort <= 0 && File.Exists(portFilePath)
        && int.TryParse(File.ReadAllText(portFilePath).Trim(), out var savedPort) && savedPort > 0)
    {
        configuredPort = savedPort;
    }

    if (configuredPort <= 0)
    {
        // Prefer the persisted distributed port over a random one so the bind/HTTPS pair stays stable.
        configuredPort = distributedConfig.BindPort > 0
            ? distributedConfig.BindPort
            : Random.Shared.Next(49152, 65536);
    }

    // Bind interface: localhost by default, 0.0.0.0 (or user-chosen) when distributed mode is on.
    var bindAddress = distributedConfig.Enabled
        ? (string.IsNullOrWhiteSpace(distributedConfig.BindAddress) ? "0.0.0.0" : distributedConfig.BindAddress)
        : "127.0.0.1";

    // Persist the resolved port back into the distributed config so the next start (and the
    // status endpoint) report the actual listener port even if the user never set one explicitly.
    distributedConfig.BindPort = configuredPort;
    distributedConfig.BindAddress = bindAddress;

    if (distributedConfig.Enabled)
    {
        // HTTPS with the persisted self-signed cert. Generate (or load) the cert eagerly so the
        // fingerprint published in runtime.json is in sync with what Kestrel actually presents.
        var tlsProvider = new TlsCertificateProvider(programDataConfigDir);
        var cert = tlsProvider.GetOrCreate(extraSanHosts: BuildSanHostsForBindAddress(bindAddress));
        distributedConfig.TlsFingerprint = CertFingerprint.Compute(cert);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Loopback HTTP stays available so existing local clients (MCP, desktop) don't have to
            // switch to HTTPS — they'd need to pin the fingerprint too, and the desktop reads
            // runtime.json from the same machine anyway.
            options.Listen(IPAddress.Loopback, configuredPort);
            // Remote-facing HTTPS listener.
            if (IPAddress.TryParse(bindAddress, out var bindIp) && !IPAddress.IsLoopback(bindIp))
            {
                options.Listen(bindIp, configuredPort, listen =>
                {
                    listen.UseHttps(cert);
                });
            }
        });
    }
    else
    {
        builder.WebHost.UseUrls($"http://localhost:{configuredPort}");
    }

    distributedStore.Save(distributedConfig);
}
else
{
    // In dev, use 5219 as the known port (matches launchSettings + Vite proxy config)
    configuredPort = 5219;
    distributedConfig.BindPort = configuredPort;
    distributedConfig.BindAddress = "127.0.0.1";
    distributedStore.Save(distributedConfig);
}

// Persist the port so the ingestor and config app can discover it
Directory.CreateDirectory(programDataConfigDir);
File.WriteAllText(portFilePath, configuredPort.ToString());

// runtime.json — the canonical "where is the API?" file the Tauri shell and MCP server
// read on startup. Contains baseUrl + apiKey + port + (phase 7a) tls fingerprint and bind
// address so the desktop UI can render the Distributed settings page without re-reading
// distributed.json itself.
var runtimeJsonPath = Path.Combine(programDataConfigDir, "runtime.json");
var legacyApiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY") ?? "";
var runtimeBaseUrl = distributedConfig.Enabled
    ? $"https://{distributedConfig.BindAddress}:{configuredPort}"
    : $"http://127.0.0.1:{configuredPort}";

File.WriteAllText(runtimeJsonPath,
    System.Text.Json.JsonSerializer.Serialize(new
    {
        baseUrl = runtimeBaseUrl,
        apiKey = legacyApiKey,
        port = configuredPort,
        bind_interface = distributedConfig.BindAddress,
        bind_port = configuredPort,
        tls_fingerprint = distributedConfig.TlsFingerprint
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

// Helper at file scope below; declared here so the lambda above can reference it.
static IEnumerable<string> BuildSanHostsForBindAddress(string bindAddress)
{
    if (string.IsNullOrWhiteSpace(bindAddress)) yield break;
    if (bindAddress == "0.0.0.0" || bindAddress == "::") yield break; // wildcard, no SAN to add
    yield return bindAddress;
}

// NLog
builder.Logging.ClearProviders();
builder.Host.UseNLog();

var startupLogger = NLog.LogManager.GetCurrentClassLogger();
startupLogger.Info("AIMemory API starting on http://localhost:{Port}", configuredPort);
startupLogger.Info("Port file written to {PortFile}", portFilePath);

// Database — dynamic provider selection
// On first run, no provider is configured — default to SQLite so the app can start and serve the setup wizard.
var dbProvider = builder.Configuration.GetValue<string>("AIMemory:DatabaseProvider");
var connectionString = Environment.GetEnvironmentVariable("AIMEMORY_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("AIMemory");
var isFirstRun = string.IsNullOrEmpty(dbProvider);

if (isFirstRun || string.Equals(dbProvider, "SQLite", StringComparison.OrdinalIgnoreCase))
{
    // Ignore any PostgreSQL-style connection string left over from bad config
    if (string.IsNullOrEmpty(connectionString) || !connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var sqliteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIMemory");
        Directory.CreateDirectory(sqliteDir);
        connectionString = $"Data Source={Path.Combine(sqliteDir, "aimemory.db")}";
    }
    builder.Services.AddDbContext<AIMemoryDbContext>(options => options.UseSqlite(connectionString));
    builder.Services.AddScoped<ISearchRepository, SqliteSearchRepository>();
    startupLogger.Info(isFirstRun
        ? "First run — no database configured, using SQLite: {ConnectionString}"
        : "Using SQLite provider: {ConnectionString}", connectionString);
}
else if (string.Equals(dbProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrEmpty(connectionString))
    {
        startupLogger.Error("PostgreSQL selected but no connection string configured. Falling back to SQLite.");
        var sqliteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIMemory");
        Directory.CreateDirectory(sqliteDir);
        connectionString = $"Data Source={Path.Combine(sqliteDir, "aimemory.db")}";
        builder.Services.AddDbContext<AIMemoryDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddScoped<ISearchRepository, SqliteSearchRepository>();
        isFirstRun = true;
    }
    else
    {
        builder.Services.AddDbContext<AIMemoryDbContext>(options => options.UseNpgsql(connectionString));
        builder.Services.AddScoped<ISearchRepository, SearchRepository>();
        startupLogger.Info("Using PostgreSQL provider");
    }
}
else
{
    startupLogger.Warn("Unknown DatabaseProvider '{Provider}', falling back to SQLite", dbProvider);
    var sqliteDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIMemory");
    Directory.CreateDirectory(sqliteDir);
    connectionString = $"Data Source={Path.Combine(sqliteDir, "aimemory.db")}";
    builder.Services.AddDbContext<AIMemoryDbContext>(options => options.UseSqlite(connectionString));
    builder.Services.AddScoped<ISearchRepository, SqliteSearchRepository>();
}

// Repositories
builder.Services.AddScoped<ISessionRepository, SessionRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IToolCallRepository, ToolCallRepository>();
builder.Services.AddScoped<IArtifactRepository, ArtifactRepository>();
builder.Services.AddScoped<IIngestionRepository, IngestionRepository>();
builder.Services.AddScoped<IVectorRepository, VectorRepository>();
builder.Services.AddScoped<ICodeIndexRepository, CodeIndexRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<IHostRepository, HostRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IFileLocationRepository, FileLocationRepository>();
builder.Services.AddScoped<IContentRepository, ContentRepository>();
builder.Services.AddScoped<IPairingRepository, PairingRepository>();

// Identity (host_id + project_id derivation, install salt)
builder.Services.AddSingleton<IInstallSaltStore>(_ => InstallSaltStore.CreateDefault());
builder.Services.AddSingleton<IHostIdProvider, HostIdProvider>();
builder.Services.AddSingleton<IProjectIdResolver, ProjectIdResolver>();
builder.Services.AddScoped<LegacyProjectMigrator>();

// Distributed-ingestion (phase 7a)
var configDirCapture = programDataConfigDir;
builder.Services.AddSingleton(sp => new DistributedConfigStore(configDirCapture));
builder.Services.AddSingleton(sp => new TlsCertificateProvider(configDirCapture));

// Service control — replaces the Tauri shell's SCM access. Endpoints under /api/admin/services/*
// inherit admin-scope auth from ApiKeyAuthMiddleware's path mapping.
builder.Services.AddSingleton<AIMemory.Api.Services.ServiceControl>();

// Code Index services
builder.Services.AddSingleton<FileFilter>();
builder.Services.AddSingleton<ILanguageParser, CSharpParser>();
builder.Services.AddSingleton<ILanguageParser, PythonParser>();
builder.Services.AddSingleton<ILanguageParser, TypeScriptParser>();
builder.Services.AddSingleton<ILanguageParser, GoParser>();
builder.Services.AddSingleton<ParserRegistry>(sp =>
    new ParserRegistry(sp.GetServices<ILanguageParser>()));
builder.Services.AddScoped<CodeIndexingService>();
builder.Services.AddScoped<CodeQueryService>();

// Cookie authentication for web UI
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "AIMemory.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Setup state singleton — tracks whether first-run wizard is needed
builder.Services.AddSingleton<SetupState>();

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
    });
    options.AddSlidingWindowLimiter("search", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.SegmentsPerWindow = 6;
    });
    options.RejectionStatusCode = 429;
});

var app = builder.Build();

// Auto-migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AIMemoryDbContext>();
    try
    {
        db.Database.Migrate();
        startupLogger.Info("Database migrations applied");

        // Phase 6 post-migrate: canonicalize fallback project ids to git ids when possible.
        var legacyMigrator = scope.ServiceProvider.GetRequiredService<LegacyProjectMigrator>();
        await legacyMigrator.RunIfPendingAsync();
    }
    catch (Exception ex)
    {
        startupLogger.Warn(ex, "Database migration failed (may need setup wizard): {Message}", ex.Message);
    }

    // Check if setup is complete
    var setupState = scope.ServiceProvider.GetRequiredService<SetupState>();
    try
    {
        setupState.IsSetupComplete = db.AdminUsers.Any();
    }
    catch
    {
        setupState.IsSetupComplete = false;
    }
}

// Middleware — request logging before auth so we see all requests including rejected ones
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AIMemory.Api.Requests");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    logger.LogInformation("{Method} {Path} from {RemoteIp}",
        context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);

    await next();

    sw.Stop();
    logger.LogInformation("{Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
        context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseRateLimiter();

// ==================== Setup Endpoints ====================

app.MapGet("/api/setup/status", (SetupState state) =>
    Results.Ok(new { needsSetup = !state.IsSetupComplete }));

app.MapPost("/api/setup/init", async (SetupRequest request, AIMemoryDbContext db, SetupState state) =>
{
    if (state.IsSetupComplete)
        return Results.BadRequest(new { error = "Setup already completed" });

    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Username and password are required" });

    if (request.Password.Length < 8)
        return Results.BadRequest(new { error = "Password must be at least 8 characters" });

    // Write config to ProgramData
    var config = new Dictionary<string, object>
    {
        ["AIMemory"] = new Dictionary<string, object>
        {
            ["DatabaseProvider"] = request.DatabaseProvider,
            ["Port"] = request.Port ?? configuredPort
        }
    };

    if (!string.IsNullOrEmpty(request.ConnectionString))
    {
        config["ConnectionStrings"] = new Dictionary<string, object>
        {
            ["AIMemory"] = request.ConnectionString
        };
    }

    Directory.CreateDirectory(programDataConfigDir);
    var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(programDataConfigPath, configJson);

    // Create admin user
    var adminUser = new AdminUser
    {
        Username = request.Username,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.AdminUsers.Add(adminUser);
    await db.SaveChangesAsync();
    state.IsSetupComplete = true;

    return Results.Ok(new { message = "Setup complete. Please log in." });
});

// ==================== Auth Endpoints ====================

app.MapPost("/api/auth/login", async (LoginRequest request, AIMemoryDbContext db, HttpContext httpContext) =>
{
    var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == request.Username);
    if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        return Results.Json(new { error = "Invalid credentials" }, statusCode: 401);

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.NameIdentifier, user.Id.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new { username = user.Username });
});

app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out" });
});

app.MapGet("/api/auth/me", (HttpContext httpContext) =>
{
    if (httpContext.User.Identity?.IsAuthenticated != true)
        return Results.Json(new { error = "Not authenticated" }, statusCode: 401);

    return Results.Ok(new
    {
        username = httpContext.User.Identity.Name,
        id = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
    });
});

// ==================== Health Endpoint ====================

app.MapGet("/api/health", async (AIMemoryDbContext db) =>
{
    try
    {
        await db.Database.CanConnectAsync();
        return Results.Ok(new { status = "healthy", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = "degraded", database = "disconnected", error = ex.Message });
    }
}).RequireRateLimiting("general");

// ==================== Admin / Tauri DB browser ====================

// Read-only inspector over the code-indexer-relevant tables. Whitelisted set only.
// Used by the Tauri DbBrowser page; admin scope required.
app.MapGet("/api/admin/tables/{name}", async (string name, int? limit, int? offset, AIMemoryDbContext db) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 500);
    var skip = Math.Max(offset ?? 0, 0);

    object[] rows;
    string[] columns;
    int total;

    switch (name)
    {
        case "code_repositories":  // legacy alias — read from projects (the SQLite view also exposes this)
        case "projects":
            columns = ["ProjectId", "DisplayName", "IdentityKind", "SourceType", "SourcePath", "FileCount", "SymbolCount", "FirstSeenAt", "LastSeenAt"];
            total = await db.Projects.CountAsync();
            rows = (await db.Projects
                .OrderByDescending(p => p.LastSeenAt)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(p => (object)new {
                    p.ProjectId, p.DisplayName, p.IdentityKind, p.SourceType, p.SourcePath,
                    p.FileCount, p.SymbolCount, p.FirstSeenAt, p.LastSeenAt
                }).ToArray();
            break;

        case "code_files":
            columns = ["ContentSha256", "Language", "FileSize", "FirstSeenAt", "LastSeenAt"];
            total = await db.CodeFiles.CountAsync();
            rows = (await db.CodeFiles
                .OrderBy(f => f.ContentSha256)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(f => (object)new {
                    f.ContentSha256, f.Language, f.FileSize, f.FirstSeenAt, f.LastSeenAt
                }).ToArray();
            break;

        case "file_locations":
            columns = ["HostId", "ProjectId", "RelPath", "ContentSha256", "Language", "FileSize", "FirstSeenAt", "LastSeenAt"];
            total = await db.FileLocations.CountAsync();
            rows = (await db.FileLocations
                .OrderBy(l => l.ProjectId).ThenBy(l => l.RelPath)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(l => (object)new {
                    l.HostId, l.ProjectId, l.RelPath, l.ContentSha256, l.Language, l.FileSize,
                    l.FirstSeenAt, l.LastSeenAt
                }).ToArray();
            break;

        case "hosts":
            columns = ["HostId", "FriendlyName", "OsKind", "IsLocal", "FirstSeenAt", "LastSeenAt"];
            total = await db.Hosts.CountAsync();
            rows = (await db.Hosts
                .OrderByDescending(h => h.LastSeenAt)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(h => (object)new {
                    h.HostId, h.FriendlyName, h.OsKind, h.IsLocal, h.FirstSeenAt, h.LastSeenAt
                }).ToArray();
            break;

        case "pairings":
            columns = ["PairingId", "HostId", "FriendlyName", "PairedAt", "LastContactAt", "IsRevoked"];
            total = await db.Pairings.CountAsync();
            rows = (await db.Pairings
                .OrderByDescending(p => p.PairedAt)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(p => (object)new {
                    p.PairingId, p.HostId, p.FriendlyName, p.PairedAt, p.LastContactAt, p.IsRevoked
                }).ToArray();
            break;

        case "code_symbols":
            columns = ["SymbolId", "ProjectId", "ContentSha256", "SymbolKey", "Name", "QualifiedName", "Kind", "StartLine", "EndLine"];
            total = await db.CodeSymbols.CountAsync();
            rows = (await db.CodeSymbols
                .OrderBy(s => s.QualifiedName)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(s => (object)new {
                    s.SymbolId, s.ProjectId, s.ContentSha256, s.SymbolKey, s.Name,
                    s.QualifiedName, s.Kind, s.StartLine, s.EndLine
                }).ToArray();
            break;

        case "ingestion_log":
            columns = ["IdempotencyKey", "EventType", "Source", "SourcePath", "Status", "MachineName", "CreatedAt"];
            total = await db.IngestionLog.CountAsync();
            rows = (await db.IngestionLog
                .OrderByDescending(l => l.CreatedAt)
                .Skip(skip).Take(take)
                .ToListAsync())
                .Select(l => (object)new {
                    l.IdempotencyKey, l.EventType, l.Source, l.SourcePath,
                    l.Status, l.MachineName, l.CreatedAt
                }).ToArray();
            break;

        default:
            return Results.NotFound(new { error = $"Table '{name}' not in whitelist" });
    }

    return Results.Ok(new { columns, rows, total });
}).RequireRateLimiting("general");

// Recent code-indexer events for the Tauri Services page activity feed.
app.MapGet("/api/ingestor/recent", async (int? limit, AIMemoryDbContext db) =>
{
    var take = Math.Clamp(limit ?? 20, 1, 200);
    var entries = await db.IngestionLog
        .Where(l => l.EventType == "CodeFileUpsert"
                 || l.EventType == "CodeSymbolBatch"
                 || l.EventType == "CodeFileDelete")
        .OrderByDescending(l => l.CreatedAt)
        .Take(take)
        .Select(l => new { l.EventType, l.SourcePath, l.MachineName, l.CreatedAt })
        .ToListAsync();
    return Results.Ok(entries);
}).RequireRateLimiting("general");

// ==================== Pairings (phase 7a) ====================
//
// Per design doc §3.2 / §4.1: secondaries register themselves with POST /api/pairings; the
// primary's UI lists with GET and revokes with DELETE. All routes require an admin-scoped
// API key (enforced by ApiKeyAuthMiddleware's /api/pairings → ["admin"] mapping).

app.MapPost("/api/pairings", async (CreatePairingRequest request,
    IPairingRepository pairingRepo, IHostRepository hostRepo, ILoggerFactory loggerFactory) =>
{
    var pairLogger = loggerFactory.CreateLogger("AIMemory.Api.Pairings");

    if (string.IsNullOrWhiteSpace(request.HostId))
        return Results.BadRequest(new { error = "HostId is required" });
    if (request.HostId.Length != 64)
        return Results.BadRequest(new { error = "HostId must be 64 hex chars" });
    if (string.IsNullOrWhiteSpace(request.FriendlyName))
        return Results.BadRequest(new { error = "FriendlyName is required" });
    if (string.IsNullOrWhiteSpace(request.OsKind))
        return Results.BadRequest(new { error = "OsKind is required" });

    // Idempotent on host_id: a re-POST for the same host with an existing active pairing
    // returns the existing record. This makes wizard re-runs safe.
    var existing = await pairingRepo.GetActiveByHostIdAsync(request.HostId);
    if (existing != null)
    {
        return Results.Ok(new PairingResponse
        {
            PairingId = existing.PairingId,
            HostId = existing.HostId,
            FriendlyName = existing.FriendlyName,
            PairedAt = existing.PairedAt,
            LastContactAt = existing.LastContactAt,
            IsRevoked = existing.IsRevoked
        });
    }

    // Ensure the host row exists. Phase 7a doesn't yet bind a per-pairing api_key (that's
    // surfaced by the admin/distributed/enable flow); ApiKeyId is left at Guid.Empty here
    // and tracked separately via the api_keys table revocation flow.
    await hostRepo.UpsertAsync(new AIMemory.Models.Entities.Host
    {
        HostId = request.HostId,
        FriendlyName = request.FriendlyName,
        OsKind = request.OsKind,
        IsLocal = false
    });

    var pairing = await pairingRepo.CreateAsync(new AIMemory.Models.Entities.Pairing
    {
        HostId = request.HostId,
        FriendlyName = request.FriendlyName,
        ApiKeyId = Guid.Empty
    });

    pairLogger.LogInformation("Pairing created: {PairingId} for host {HostId} ({FriendlyName})",
        pairing.PairingId, pairing.HostId, pairing.FriendlyName);

    return Results.Created($"/api/pairings/{pairing.PairingId}", new PairingResponse
    {
        PairingId = pairing.PairingId,
        HostId = pairing.HostId,
        FriendlyName = pairing.FriendlyName,
        PairedAt = pairing.PairedAt,
        LastContactAt = pairing.LastContactAt,
        IsRevoked = pairing.IsRevoked
    });
}).RequireRateLimiting("general");

app.MapGet("/api/pairings", async (bool? includeRevoked, IPairingRepository pairingRepo) =>
{
    var pairings = await pairingRepo.ListAsync(includeRevoked ?? false);
    return Results.Ok(pairings.Select(p => new PairingResponse
    {
        PairingId = p.PairingId,
        HostId = p.HostId,
        FriendlyName = p.FriendlyName,
        PairedAt = p.PairedAt,
        LastContactAt = p.LastContactAt,
        IsRevoked = p.IsRevoked
    }));
}).RequireRateLimiting("general");

app.MapDelete("/api/pairings/{id:guid}", async (Guid id,
    IPairingRepository pairingRepo, IApiKeyRepository keyRepo, ILoggerFactory loggerFactory) =>
{
    var pairLogger = loggerFactory.CreateLogger("AIMemory.Api.Pairings");
    var pairing = await pairingRepo.GetAsync(id);
    if (pairing == null)
        return Results.NotFound(new { error = "Pairing not found" });

    var revoked = await pairingRepo.RevokeAsync(id);
    if (!revoked)
        return Results.Ok(new { message = "Pairing was already revoked" });

    // Cascade: deactivate the linked api key so the secondary's subsequent requests 401.
    if (pairing.ApiKeyId != Guid.Empty)
    {
        try
        {
            await keyRepo.RevokeAsync(pairing.ApiKeyId);
        }
        catch (Exception ex)
        {
            pairLogger.LogWarning(ex, "Failed to revoke linked API key {KeyId} for pairing {PairingId}",
                pairing.ApiKeyId, id);
        }
    }

    pairLogger.LogInformation("Pairing {PairingId} revoked for host {HostId}", id, pairing.HostId);
    return Results.Ok(new { message = "Pairing revoked" });
}).RequireRateLimiting("general");

// ==================== Distributed admin toggle (phase 7a) ====================

app.MapPost("/api/admin/distributed/enable", async (
    string? bindInterface,
    DistributedConfigStore configStore, TlsCertificateProvider tlsProvider,
    IApiKeyRepository keyRepo, HttpContext httpContext, ILoggerFactory loggerFactory) =>
{
    var distLogger = loggerFactory.CreateLogger("AIMemory.Api.Distributed");

    // Phase 11: validate the user-supplied bindInterface before mutating any state. Empty/null
    // falls through to the legacy behavior (default to 0.0.0.0). A loopback selection is honored
    // but logged as a warning — it defeats the point of distributed mode but isn't malformed.
    var bindResult = BindInterfaceValidator.Validate(bindInterface);
    if (!bindResult.IsEmpty && !bindResult.IsValid)
    {
        distLogger.LogWarning("Rejecting distributed enable: invalid bindInterface '{Bind}'", bindInterface);
        return Results.BadRequest(new { error = bindResult.ErrorMessage });
    }
    string? requestedBind = bindResult.IsValid ? bindResult.NormalizedAddress : null;
    if (bindResult.IsValid && bindResult.IsLoopback)
    {
        distLogger.LogWarning(
            "Distributed mode enabled with loopback bindInterface {Bind} — remote ingestors will not be reachable",
            requestedBind);
    }

    var cfg = configStore.Load();
    var wasEnabled = cfg.Enabled;
    var previousBind = cfg.BindAddress;

    cfg.Enabled = true;
    if (requestedBind != null)
    {
        cfg.BindAddress = requestedBind;
    }
    else if (string.IsNullOrWhiteSpace(cfg.BindAddress) || cfg.BindAddress == "127.0.0.1")
    {
        cfg.BindAddress = "0.0.0.0";
    }

    // Generate (or reuse) the cert so the fingerprint we return matches what Kestrel will
    // present after restart. SAN includes loopback + the bind interface (when not wildcard).
    var sanHosts = new List<string>();
    if (cfg.BindAddress != "0.0.0.0" && cfg.BindAddress != "::")
        sanHosts.Add(cfg.BindAddress);
    var cert = tlsProvider.GetOrCreate(sanHosts);
    cfg.TlsFingerprint = CertFingerprint.Compute(cert);
    configStore.Save(cfg);

    // Issue a fresh ingest-scoped key. One-time reveal — primary stores only the SHA-256.
    var rawKey = "aimemory_" + Convert.ToHexStringLower(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    var keyHash = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));

    await keyRepo.CreateAsync(new ApiKey
    {
        Name = $"distributed-ingest-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
        KeyHash = keyHash,
        KeyPrefix = rawKey[8..16],
        Scopes = ["ingest"],
        IsActive = true,
        CreatedBy = httpContext.User.Identity?.Name ?? "admin/distributed/enable"
    });

    distLogger.LogInformation("Distributed mode enabled (was: {Was}) — bind {Bind}, fingerprint {Fingerprint}",
        wasEnabled, cfg.BindAddress, cfg.TlsFingerprint);

    var endpoint = $"https://{cfg.BindAddress}:{cfg.BindPort}";
    return Results.Ok(new DistributedEnableResponse
    {
        Endpoint = endpoint,
        Fingerprint = cfg.TlsFingerprint,
        ApiKey = rawKey,
        // Persisted state changes take effect on the next service start. The desktop wizard
        // is responsible for triggering the restart via the Tauri service control layer.
        RestartRequired = !wasEnabled
            || !string.Equals(previousBind, cfg.BindAddress, StringComparison.OrdinalIgnoreCase)
    });
}).RequireRateLimiting("general");

app.MapPost("/api/admin/distributed/disable", (
    DistributedConfigStore configStore, ILoggerFactory loggerFactory) =>
{
    var distLogger = loggerFactory.CreateLogger("AIMemory.Api.Distributed");
    var cfg = configStore.Load();
    var wasEnabled = cfg.Enabled;
    cfg.Enabled = false;
    cfg.BindAddress = "127.0.0.1";
    // Keep the fingerprint cached so we can restore it without regenerating if the user
    // re-enables; clearing it would force a re-pair on every secondary, which is a sharp
    // edge for a soft toggle. Existing pairings remain in the DB but are unreachable
    // until the listener rebinds.
    configStore.Save(cfg);

    distLogger.LogInformation("Distributed mode disabled (was: {Was})", wasEnabled);
    return Results.Ok(new { message = "Distributed mode disabled. Restart the service to apply.", restartRequired = wasEnabled });
}).RequireRateLimiting("general");

app.MapGet("/api/admin/distributed/status", async (
    DistributedConfigStore configStore, IPairingRepository pairingRepo) =>
{
    var cfg = configStore.Load();
    var pairings = await pairingRepo.ListAsync(includeRevoked: false);
    return Results.Ok(new DistributedStatusResponse
    {
        Enabled = cfg.Enabled,
        BindAddress = cfg.BindAddress,
        BindPort = cfg.BindPort,
        Endpoint = cfg.Enabled ? $"https://{cfg.BindAddress}:{cfg.BindPort}" : null,
        Fingerprint = cfg.Enabled ? cfg.TlsFingerprint : null,
        PairedHostCount = pairings.Count
    });
}).RequireRateLimiting("general");

// ==================== Service Control (post-Tauri replacement for the SCM surface) ====================
//
// Replaces the Tauri shell's service_status/start/stop/restart commands. The API service runs as
// LocalSystem on Windows (per the installer in phase 10) so it has the SCM access required to
// control aimemory-api and aimemory-ingestor without a separate elevation step.

app.MapGet("/api/admin/services/{name}/status", async (
    string name, AIMemory.Api.Services.ServiceControl svc, CancellationToken ct) =>
{
    if (!svc.IsAllowed(name)) return Results.NotFound(new { error = $"Unknown service '{name}'" });
    var status = await svc.GetStatusAsync(name, ct);
    return Results.Ok(status);
}).RequireRateLimiting("general");

app.MapPost("/api/admin/services/{name}/start", async (
    string name, AIMemory.Api.Services.ServiceControl svc, CancellationToken ct) =>
{
    if (!svc.IsAllowed(name)) return Results.NotFound(new { error = $"Unknown service '{name}'" });
    var result = await svc.StartAsync(name, ct);
    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 500);
}).RequireRateLimiting("general");

app.MapPost("/api/admin/services/{name}/stop", async (
    string name, AIMemory.Api.Services.ServiceControl svc, CancellationToken ct) =>
{
    if (!svc.IsAllowed(name)) return Results.NotFound(new { error = $"Unknown service '{name}'" });
    var result = await svc.StopAsync(name, ct);
    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 500);
}).RequireRateLimiting("general");

app.MapPost("/api/admin/services/{name}/restart", async (
    string name, AIMemory.Api.Services.ServiceControl svc, CancellationToken ct) =>
{
    if (!svc.IsAllowed(name)) return Results.NotFound(new { error = $"Unknown service '{name}'" });
    var result = await svc.RestartAsync(name, ct);
    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 500);
}).RequireRateLimiting("general");

// ==================== Network interfaces (post-Tauri replacement for list_network_interfaces) ====================
//
// Suggests bind addresses for the Distributed enable flow. Always includes the wildcard
// (0.0.0.0) plus any up, non-loopback IPv4 unicast addresses. The picker treats the list as
// suggestions — the user can type any IPv4 and BindInterfaceValidator decides if it's accepted.
app.MapGet("/api/admin/network-interfaces", () =>
{
    var results = new List<object>
    {
        new { name = "Any (0.0.0.0)", address = "0.0.0.0" }
    };

    try
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                results.Add(new { name = $"{nic.Name} ({addr.Address})", address = addr.Address.ToString() });
            }
        }
    }
    catch
    {
        // Best-effort: if enumeration fails we still return the wildcard so the UI picker isn't empty.
    }

    return Results.Ok(results);
}).RequireRateLimiting("general");

// ==================== Filesystem path validation (post-Tauri replacement for pick_folder) ====================
//
// Replaces the native folder picker with text-input + server-side validation. Web browsers can't
// portably surface a directory picker (input[type=file] webkitdirectory uploads contents, doesn't
// return a path), so the Repos add-repo flow now takes a typed path and validates it here.
app.MapPost("/api/admin/fs/validate-path", (ValidatePathRequest request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Path))
        return Results.BadRequest(new { error = "path is required" });

    string fullPath;
    try { fullPath = Path.GetFullPath(request.Path); }
    catch (Exception ex) { return Results.BadRequest(new { error = $"invalid path: {ex.Message}" }); }

    var exists = Directory.Exists(fullPath);
    var isGitRepo = exists && (Directory.Exists(Path.Combine(fullPath, ".git"))
                              || File.Exists(Path.Combine(fullPath, ".git"))); // worktrees use a file
    return Results.Ok(new
    {
        path = fullPath,
        exists,
        isDirectory = exists,
        isGitRepo
    });
}).RequireRateLimiting("general");

// ==================== Stats Endpoint ====================

app.MapGet("/api/stats", async (AIMemoryDbContext db) =>
{
    var today = DateTimeOffset.UtcNow.Date;
    var thirtyDaysAgo = today.AddDays(-30);

    var totalSessions = await db.Sessions.CountAsync();
    var totalMessages = await db.Messages.CountAsync();
    var tokenStats = await db.Messages
        .GroupBy(_ => 1)
        .Select(g => new
        {
            TotalTokensIn = g.Sum(m => (long)(m.TokenIn ?? 0)),
            TotalTokensOut = g.Sum(m => (long)(m.TokenOut ?? 0)),
            TotalCostUsd = g.Sum(m => m.CostUsd ?? 0m)
        })
        .FirstOrDefaultAsync();

    var sessionsToday = await db.Sessions
        .CountAsync(s => s.CreatedAt >= new DateTimeOffset(today, TimeSpan.Zero));

    var dailySessionStats = (await db.Sessions
        .Where(s => s.CreatedAt >= new DateTimeOffset(thirtyDaysAgo, TimeSpan.Zero))
        .Select(s => s.CreatedAt)
        .ToListAsync())
        .GroupBy(d => d.Date)
        .Select(g => new { Date = g.Key, Count = g.Count() })
        .ToList();

    var dailyMessageStats = (await db.Messages
        .Where(m => m.CreatedAt >= new DateTimeOffset(thirtyDaysAgo, TimeSpan.Zero))
        .Select(m => m.CreatedAt)
        .ToListAsync())
        .GroupBy(d => d.Date)
        .Select(g => new { Date = g.Key, Count = g.Count() })
        .ToList();

    var dailyStats = Enumerable.Range(0, 30)
        .Select(i => thirtyDaysAgo.AddDays(i))
        .Select(date => new DailyStats
        {
            Date = date.ToString("yyyy-MM-dd"),
            Sessions = dailySessionStats.FirstOrDefault(d => d.Date == date)?.Count ?? 0,
            Messages = dailyMessageStats.FirstOrDefault(d => d.Date == date)?.Count ?? 0
        })
        .ToList();

    return Results.Ok(new StatsResponse
    {
        TotalSessions = totalSessions,
        TotalMessages = totalMessages,
        TotalTokensIn = tokenStats?.TotalTokensIn ?? 0,
        TotalTokensOut = tokenStats?.TotalTokensOut ?? 0,
        TotalCostUsd = tokenStats?.TotalCostUsd ?? 0m,
        SessionsToday = sessionsToday,
        DailyStats = dailyStats
    });
}).RequireRateLimiting("general");

// ==================== Session Endpoints ====================

app.MapPost("/api/sessions", async (CreateSessionRequest request, ISessionRepository repo) =>
{
    var session = new Session
    {
        Title = request.Title,
        Project = request.Project,
        Repo = request.Repo,
        Branch = request.Branch,
        Tags = request.Tags ?? [],
        Source = request.Source,
        ExternalId = request.ExternalId
    };

    var created = await repo.CreateAsync(session);
    return Results.Created($"/api/sessions/{created.SessionId}", new { sessionId = created.SessionId });
}).RequireRateLimiting("general");

app.MapGet("/api/sessions", async (string? project, string? repo, string? source, string? tag,
    DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset,
    ISessionRepository sessionRepo, IMessageRepository messageRepo) =>
{
    var sessions = await sessionRepo.ListAsync(project, repo, source, tag, from, to, limit ?? 50, offset ?? 0);

    var response = new List<SessionResponse>();
    foreach (var session in sessions)
    {
        var messages = await messageRepo.GetBySessionAsync(session.SessionId, 1);
        response.Add(new SessionResponse
        {
            SessionId = session.SessionId,
            ExternalId = session.ExternalId,
            Title = session.Title,
            Project = session.Project,
            Repo = session.Repo,
            Branch = session.Branch,
            Tags = session.Tags,
            Source = session.Source,
            IsArchived = session.IsArchived,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            MessageCount = messages.Count > 0 ? await GetMessageCount(messageRepo, session.SessionId) : 0
        });
    }
    return Results.Ok(response);
}).RequireRateLimiting("general");

app.MapGet("/api/sessions/{sessionId:guid}", async (Guid sessionId, int? limit,
    bool? includeToolCalls, bool? includeArtifacts,
    ISessionRepository sessionRepo, IMessageRepository messageRepo,
    IToolCallRepository toolCallRepo, IArtifactRepository artifactRepo) =>
{
    var session = await sessionRepo.GetByIdAsync(sessionId);
    if (session is null) return Results.NotFound(new { error = "Session not found" });

    var messages = await messageRepo.GetBySessionAsync(sessionId, limit ?? 100);

    var response = new SessionResponse
    {
        SessionId = session.SessionId,
        ExternalId = session.ExternalId,
        Title = session.Title,
        Project = session.Project,
        Repo = session.Repo,
        Branch = session.Branch,
        Tags = session.Tags,
        Source = session.Source,
        IsArchived = session.IsArchived,
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        MessageCount = messages.Count,
        Messages = messages.Select(m => new MessageResponse
        {
            MessageId = m.MessageId,
            SessionId = m.SessionId,
            Role = m.Role,
            Content = m.Content,
            Provider = m.Provider,
            Model = m.Model,
            TokenIn = m.TokenIn,
            TokenOut = m.TokenOut,
            CostUsd = m.CostUsd,
            LatencyMs = m.LatencyMs,
            CreatedAt = m.CreatedAt
        }).ToList()
    };

    if (includeToolCalls != false)
    {
        var toolCalls = await toolCallRepo.GetBySessionAsync(sessionId);
        response.ToolCalls = toolCalls.Select(tc => new ToolCallResponse
        {
            ToolCallId = tc.ToolCallId,
            SessionId = tc.SessionId,
            ToolName = tc.ToolName,
            ArgumentsJson = tc.ArgumentsJson,
            ResultJson = tc.ResultJson,
            CreatedAt = tc.CreatedAt
        }).ToList();
    }

    if (includeArtifacts != false)
    {
        var artifacts = await artifactRepo.GetBySessionAsync(sessionId);
        response.Artifacts = artifacts.Select(a => new ArtifactResponse
        {
            ArtifactId = a.ArtifactId,
            SessionId = a.SessionId,
            Type = a.Type,
            PathOrUrl = a.PathOrUrl,
            Hash = a.Hash,
            MetadataJson = a.MetadataJson,
            CreatedAt = a.CreatedAt
        }).ToList();
    }

    return Results.Ok(response);
}).RequireRateLimiting("general");

app.MapPost("/api/sessions/{sessionId:guid}/messages", async (Guid sessionId, AppendMessageRequest request,
    ISessionRepository sessionRepo, IMessageRepository messageRepo) =>
{
    var session = await sessionRepo.GetByIdAsync(sessionId);
    if (session is null) return Results.NotFound(new { error = "Session not found" });

    var message = new Message
    {
        SessionId = sessionId,
        Role = request.Role,
        Content = request.Content,
        Provider = request.Provider,
        Model = request.Model,
        RequestId = request.RequestId,
        TokenIn = request.TokenIn,
        TokenOut = request.TokenOut,
        CostUsd = request.CostUsd,
        LatencyMs = request.LatencyMs,
        ExternalId = request.ExternalId
    };

    var created = await messageRepo.AppendAsync(message);
    return Results.Created($"/api/sessions/{sessionId}/messages/{created.MessageId}",
        new { messageId = created.MessageId });
}).RequireRateLimiting("general");

app.MapPost("/api/sessions/{sessionId:guid}/toolcalls", async (Guid sessionId, AppendToolCallRequest request,
    ISessionRepository sessionRepo, IToolCallRepository toolCallRepo) =>
{
    var session = await sessionRepo.GetByIdAsync(sessionId);
    if (session is null) return Results.NotFound(new { error = "Session not found" });

    var toolCall = new ToolCall
    {
        SessionId = sessionId,
        ToolName = request.ToolName,
        ArgumentsJson = request.ArgumentsJson.HasValue
            ? JsonDocument.Parse(request.ArgumentsJson.Value.GetRawText())
            : null,
        ResultJson = request.ResultJson.HasValue
            ? JsonDocument.Parse(request.ResultJson.Value.GetRawText())
            : null,
        ExternalId = request.ExternalId
    };

    var created = await toolCallRepo.AppendAsync(toolCall);
    return Results.Created($"/api/sessions/{sessionId}/toolcalls/{created.ToolCallId}",
        new { toolCallId = created.ToolCallId });
}).RequireRateLimiting("general");

app.MapPost("/api/sessions/{sessionId:guid}/artifacts", async (Guid sessionId, AppendArtifactRequest request,
    ISessionRepository sessionRepo, IArtifactRepository artifactRepo) =>
{
    var session = await sessionRepo.GetByIdAsync(sessionId);
    if (session is null) return Results.NotFound(new { error = "Session not found" });

    var artifact = new Artifact
    {
        SessionId = sessionId,
        Type = request.Type,
        PathOrUrl = request.PathOrUrl,
        Hash = request.Hash,
        MetadataJson = request.MetadataJson.HasValue
            ? JsonDocument.Parse(request.MetadataJson.Value.GetRawText())
            : null,
        ExternalId = request.ExternalId
    };

    var created = await artifactRepo.AppendAsync(artifact);
    return Results.Created($"/api/sessions/{sessionId}/artifacts/{created.ArtifactId}",
        new { artifactId = created.ArtifactId });
}).RequireRateLimiting("general");

// ==================== Search Endpoint ====================

app.MapGet("/api/search", async (string q, string? project, string? repo, string? source, int? limit, int? offset,
    ISearchRepository searchRepo) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Query parameter 'q' is required" });

    var results = await searchRepo.SearchAsync(q, project, repo, source, limit ?? 10, offset ?? 0);
    return Results.Ok(results);
}).RequireRateLimiting("search");

// ==================== Ingestion Endpoints ====================

app.MapGet("/api/ingestion-log", async (string? source, string? eventType, int? limit, int? offset,
    IIngestionRepository ingestionRepo) =>
{
    var entries = await ingestionRepo.ListAsync(source, eventType, limit ?? 50, offset ?? 0);
    return Results.Ok(entries);
}).RequireRateLimiting("general");

app.MapPost("/api/ingest/batch", async (BatchIngestRequest request, AIMemoryDbContext db,
    ISessionRepository sessionRepo, IMessageRepository messageRepo,
    IToolCallRepository toolCallRepo, IArtifactRepository artifactRepo,
    IIngestionRepository ingestionRepo, ICodeIndexRepository codeIndexRepo,
    IHostRepository hostRepo,
    IHostIdProvider hostIdProvider, IProjectIdResolver projectIdResolver,
    ILoggerFactory loggerFactory) =>
{
    var batchLogger = loggerFactory.CreateLogger("AIMemory.Api.Ingest");
    batchLogger.LogInformation("Batch received: {Count} events from client={ClientId} source={Source} host={HostId}",
        request.Events.Count, request.ClientId, request.Source, request.HostId);

    // Phase 7a: validate HostId against the hosts table when supplied. Empty/null is allowed
    // (v1 single-machine ingest path) — those are treated as the local host below. A non-empty
    // value that doesn't match any known host is rejected with 403 to prevent secondaries from
    // claiming arbitrary host_id values for events they emit.
    if (!string.IsNullOrEmpty(request.HostId))
    {
        var knownHost = await hostRepo.GetAsync(request.HostId);
        if (knownHost == null)
        {
            batchLogger.LogWarning("Rejecting batch with unknown HostId {HostId}", request.HostId);
            return Results.Json(
                new { error = $"Unknown HostId: {request.HostId}. Pair this host with the primary first." },
                statusCode: 403);
        }
    }

    var eventResults = new List<IngestEventResult>();
    var existingKeys = await ingestionRepo.FilterExistingKeysAsync(request.Events.Select(e => e.IdempotencyKey));

    foreach (var evt in request.Events)
    {
        if (existingKeys.Contains(evt.IdempotencyKey))
        {
            eventResults.Add(new IngestEventResult
            {
                IdempotencyKey = evt.IdempotencyKey,
                Status = "duplicate"
            });
            continue;
        }

        try
        {
            switch (evt.Type)
            {
                case "SessionUpsert":
                    var sessionEvt = JsonSerializer.Deserialize<SessionUpsertEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (sessionEvt != null)
                    {
                        await sessionRepo.UpsertByExternalIdAsync(new Session
                        {
                            ExternalId = sessionEvt.SessionExternalId,
                            Title = sessionEvt.Title ?? "Untitled",
                            Project = sessionEvt.Project,
                            Repo = sessionEvt.Repo,
                            Branch = sessionEvt.Branch,
                            Tags = sessionEvt.Tags ?? [],
                            Source = sessionEvt.Source,
                            MachineName = request.MachineName
                        });
                    }
                    break;

                case "MessageAppend":
                    var msgEvt = JsonSerializer.Deserialize<MessageAppendEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (msgEvt != null)
                    {
                        var session = await sessionRepo.GetByExternalIdAsync(msgEvt.SessionExternalId);
                        if (session != null)
                        {
                            await messageRepo.AppendAsync(new Message
                            {
                                SessionId = session.SessionId,
                                ExternalId = msgEvt.MessageExternalId,
                                Role = msgEvt.Role,
                                Content = msgEvt.Content,
                                Provider = msgEvt.Provider,
                                Model = msgEvt.Model,
                                TokenIn = msgEvt.TokenIn,
                                TokenOut = msgEvt.TokenOut,
                                CostUsd = msgEvt.CostUsd,
                                LatencyMs = msgEvt.LatencyMs,
                                CreatedAt = msgEvt.CreatedAt
                            });
                        }
                    }
                    break;

                case "ToolCallAppend":
                    var tcEvt = JsonSerializer.Deserialize<ToolCallAppendEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (tcEvt != null)
                    {
                        var tcSession = await sessionRepo.GetByExternalIdAsync(tcEvt.SessionExternalId);
                        if (tcSession != null)
                        {
                            await toolCallRepo.AppendAsync(new ToolCall
                            {
                                SessionId = tcSession.SessionId,
                                ExternalId = tcEvt.ToolCallExternalId,
                                ToolName = tcEvt.ToolName,
                                ArgumentsJson = tcEvt.ArgumentsJson.HasValue
                                    ? JsonDocument.Parse(tcEvt.ArgumentsJson.Value.GetRawText()) : null,
                                ResultJson = tcEvt.ResultJson.HasValue
                                    ? JsonDocument.Parse(tcEvt.ResultJson.Value.GetRawText()) : null,
                                CreatedAt = tcEvt.CreatedAt
                            });
                        }
                    }
                    break;

                case "ArtifactAppend":
                    var artEvt = JsonSerializer.Deserialize<ArtifactAppendEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (artEvt != null)
                    {
                        var artSession = await sessionRepo.GetByExternalIdAsync(artEvt.SessionExternalId);
                        if (artSession != null)
                        {
                            await artifactRepo.AppendAsync(new Artifact
                            {
                                SessionId = artSession.SessionId,
                                ExternalId = artEvt.ArtifactExternalId,
                                Type = artEvt.Type,
                                PathOrUrl = artEvt.PathOrUrl,
                                Hash = artEvt.Hash,
                                MetadataJson = artEvt.MetadataJson.HasValue
                                    ? JsonDocument.Parse(artEvt.MetadataJson.Value.GetRawText()) : null,
                                CreatedAt = artEvt.CreatedAt
                            });
                        }
                    }
                    break;

                case "CodeFileUpsert":
                    var codeFileEvt = JsonSerializer.Deserialize<CodeFileUpsertEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (codeFileEvt != null)
                    {
                        // Phase 11: prefer the batch's ProjectId (the secondary's canonical project_id
                        // resolved per design §2.2) when supplied. v2 secondaries always populate it
                        // and that's the dedup invariant — same project_id from two hosts → same
                        // projects row. v1 batches and the local single-machine ingestor still flow
                        // through the name-based ResolveOrCreate path for backcompat.
                        Project project;
                        if (!string.IsNullOrEmpty(request.ProjectId))
                        {
                            var existingById = await codeIndexRepo.GetProjectAsync(request.ProjectId);
                            project = existingById ?? await codeIndexRepo.UpsertProjectAsync(new Project
                            {
                                ProjectId = request.ProjectId,
                                DisplayName = codeFileEvt.RepositoryName,
                                IdentityKind = "git", // optimistic; overwritten on next git-aware ingest
                                SourceType = codeFileEvt.SourceType,
                                SourcePath = codeFileEvt.SourcePath
                            });
                        }
                        else
                        {
                            project = await ResolveOrCreateProjectAsync(
                                codeIndexRepo, projectIdResolver, hostIdProvider,
                                codeFileEvt.RepositoryName, codeFileEvt.SourceType, codeFileEvt.SourcePath);
                        }

                        // Phase 11: file_locations are attributed to the *batch's* host_id, not the
                        // primary's local host. v1 batches with no HostId still fall back to local.
                        var attributionHostId = !string.IsNullOrEmpty(request.HostId)
                            ? request.HostId
                            : hostIdProvider.GetHostId();
                        // RelPath is the v2 path field; FilePath is the v1 fallback (design §3.3).
                        var relPath = !string.IsNullOrEmpty(codeFileEvt.RelPath) ? codeFileEvt.RelPath : codeFileEvt.FilePath;
                        await codeIndexRepo.UpsertFileAsync(
                            attributionHostId, project.ProjectId, relPath,
                            codeFileEvt.Language, codeFileEvt.FileSize, codeFileEvt.ContentHash);

                        await codeIndexRepo.UpdateProjectStatsAsync(project.ProjectId, attributionHostId);
                    }
                    break;

                case "CodeFileDelete":
                    var codeDeleteEvt = JsonSerializer.Deserialize<CodeFileDeleteEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (codeDeleteEvt != null)
                    {
                        // Prefer batch's ProjectId, fall back to the legacy name lookup for v1.
                        Project? delProject = !string.IsNullOrEmpty(request.ProjectId)
                            ? await codeIndexRepo.GetProjectAsync(request.ProjectId)
                            : await codeIndexRepo.GetProjectByNameAsync(codeDeleteEvt.RepositoryName);
                        if (delProject != null)
                        {
                            var attributionHostId = !string.IsNullOrEmpty(request.HostId)
                                ? request.HostId
                                : hostIdProvider.GetHostId();
                            var relPath = !string.IsNullOrEmpty(codeDeleteEvt.RelPath) ? codeDeleteEvt.RelPath : codeDeleteEvt.FilePath;
                            var removed = await codeIndexRepo.DeleteFileAsync(
                                attributionHostId, delProject.ProjectId, relPath);
                            if (removed)
                                await codeIndexRepo.UpdateProjectStatsAsync(delProject.ProjectId, attributionHostId);
                        }
                        // Missing project or missing file is not an error — the delete is idempotent.
                    }
                    break;

                case "CodeSymbolBatch":
                    var symBatchEvt = JsonSerializer.Deserialize<CodeSymbolBatchEvent>(
                        evt.Payload.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (symBatchEvt != null)
                    {
                        Project? symProject = !string.IsNullOrEmpty(request.ProjectId)
                            ? await codeIndexRepo.GetProjectAsync(request.ProjectId)
                            : await codeIndexRepo.GetProjectByNameAsync(symBatchEvt.RepositoryName);
                        if (symProject != null)
                        {
                            var attributionHostId = !string.IsNullOrEmpty(request.HostId)
                                ? request.HostId
                                : hostIdProvider.GetHostId();
                            var locations = await codeIndexRepo.GetFileTreeAsync(symProject.ProjectId, attributionHostId);
                            var location = locations.FirstOrDefault(l => l.RelPath == symBatchEvt.FilePath);
                            if (location != null)
                            {
                                var symbols = symBatchEvt.Symbols.Select(s => new CodeSymbol
                                {
                                    SymbolKey = s.SymbolKey,
                                    Name = s.Name,
                                    QualifiedName = s.QualifiedName,
                                    Kind = s.Kind,
                                    Signature = s.Signature,
                                    StartLine = s.StartLine,
                                    EndLine = s.EndLine,
                                    StartByte = s.StartByte,
                                    EndByte = s.EndByte,
                                    ParentSymbolKey = s.ParentSymbolKey
                                }).ToList();

                                await codeIndexRepo.UpsertSymbolsAsync(
                                    location.ContentSha256, symProject.ProjectId, symbols);
                                await codeIndexRepo.UpdateProjectStatsAsync(symProject.ProjectId, attributionHostId);
                            }
                        }
                    }
                    break;

                default:
                    eventResults.Add(new IngestEventResult
                    {
                        IdempotencyKey = evt.IdempotencyKey,
                        Status = "error",
                        Error = $"Unknown event type: {evt.Type}"
                    });
                    continue;
            }

            await ingestionRepo.LogAsync(new IngestionLogEntry
            {
                IdempotencyKey = evt.IdempotencyKey,
                EventType = evt.Type,
                Source = request.Source,
                SourcePath = evt.IdempotencyKey.Split('|').ElementAtOrDefault(1) ?? "",
                RecordOffset = long.TryParse(evt.IdempotencyKey.Split('|').ElementAtOrDefault(2), out var off) ? off : 0,
                MachineName = request.MachineName
            });

            eventResults.Add(new IngestEventResult
            {
                IdempotencyKey = evt.IdempotencyKey,
                Status = "ok"
            });
        }
        catch (Exception ex)
        {
            batchLogger.LogError(ex, "Event {Type} failed: key={Key}", evt.Type, evt.IdempotencyKey);
            eventResults.Add(new IngestEventResult
            {
                IdempotencyKey = evt.IdempotencyKey,
                Status = "error",
                Error = ex.Message
            });
        }
    }

    var succeeded = eventResults.Count(r => r.Status == "ok");
    var duplicates = eventResults.Count(r => r.Status == "duplicate");
    var failed = eventResults.Count(r => r.Status == "error");
    batchLogger.LogInformation("Batch complete: {Succeeded} ok, {Duplicates} duplicates, {Failed} failed",
        succeeded, duplicates, failed);

    return Results.Ok(new BatchIngestResponse
    {
        Total = eventResults.Count,
        Succeeded = succeeded,
        Duplicates = duplicates,
        Failed = failed,
        Results = eventResults
    });
}).RequireRateLimiting("general");

// ==================== Code Index Endpoints ====================
//
// Phase 6: route parameter is a project_id (64-char lowercase hex SHA-256), not a Guid.
// Path shapes are unchanged so MCP clients keep working — only the id format changed.

static CodeRepoResponse ToRepoResponse(Project p) => new()
{
    ProjectId = p.ProjectId,
    Name = p.DisplayName,
    SourceType = p.SourceType,
    SourcePath = p.SourcePath,
    FileCount = p.FileCount,
    SymbolCount = p.SymbolCount,
    IndexedAt = p.FirstSeenAt,
    UpdatedAt = p.LastSeenAt
};

app.MapPost("/api/code/index-folder", async (IndexFolderRequest request, CodeIndexingService indexer) =>
{
    if (string.IsNullOrWhiteSpace(request.FolderPath))
        return Results.BadRequest(new { error = "FolderPath is required" });

    var project = await indexer.IndexLocalFolderAsync(request.FolderPath, request.Name);
    return Results.Ok(ToRepoResponse(project));
}).RequireRateLimiting("general");

app.MapGet("/api/code/repos", async (CodeQueryService query) =>
{
    var repos = await query.ListRepositoriesAsync();
    return Results.Ok(repos);
}).RequireRateLimiting("general");

app.MapGet("/api/code/repos/{repoId}", async (string repoId, ICodeIndexRepository codeRepo) =>
{
    var project = await codeRepo.GetProjectAsync(repoId);
    if (project == null) return Results.NotFound(new { error = "Repository not found" });
    return Results.Ok(ToRepoResponse(project));
}).RequireRateLimiting("general");

app.MapDelete("/api/code/repos/{repoId}", async (string repoId, ICodeIndexRepository codeRepo) =>
{
    await codeRepo.DeleteProjectAsync(repoId);
    return Results.Ok(new { message = "Repository deleted" });
}).RequireRateLimiting("general");

app.MapPost("/api/code/repos/{repoId}/reindex", async (string repoId, CodeIndexingService indexer) =>
{
    var project = await indexer.ReindexAsync(repoId);
    return Results.Ok(ToRepoResponse(project));
}).RequireRateLimiting("general");

app.MapGet("/api/code/repos/{repoId}/tree", async (string repoId, CodeQueryService query) =>
{
    var tree = await query.GetFileTreeAsync(repoId);
    if (tree == null) return Results.NotFound(new { error = "Repository not found" });
    return Results.Ok(tree);
}).RequireRateLimiting("general");

app.MapGet("/api/code/repos/{repoId}/outline", async (string repoId, string? file, CodeQueryService query) =>
{
    if (!string.IsNullOrEmpty(file))
    {
        var outline = await query.GetFileOutlineAsync(repoId, file);
        if (outline == null) return Results.NotFound(new { error = "Repository not found" });
        return Results.Ok(outline);
    }

    var repoOutline = await query.GetRepoOutlineAsync(repoId);
    if (repoOutline == null) return Results.NotFound(new { error = "Repository not found" });
    return Results.Ok(repoOutline);
}).RequireRateLimiting("general");

app.MapGet("/api/code/repos/{repoId}/symbol", async (string repoId, string key, CodeQueryService query) =>
{
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new { error = "Symbol key is required" });

    var symbol = await query.GetSymbolAsync(repoId, key);
    if (symbol == null) return Results.NotFound(new { error = "Symbol not found" });
    return Results.Ok(symbol);
}).RequireRateLimiting("general");

app.MapPost("/api/code/repos/{repoId}/symbols", async (string repoId, List<string> symbolKeys, CodeQueryService query) =>
{
    var symbols = await query.GetSymbolsAsync(repoId, symbolKeys);
    return Results.Ok(symbols);
}).RequireRateLimiting("general");

app.MapGet("/api/code/search/symbols", async (string q, string? repo, string? kind, int? limit, CodeQueryService query) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Query parameter 'q' is required" });

    var results = await query.SearchSymbolsAsync(q, repo, kind, limit ?? 20);
    return Results.Ok(results);
}).RequireRateLimiting("search");

app.MapGet("/api/code/search/text", async (string q, string? repo, string? file, int? limit, CodeQueryService query) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Query parameter 'q' is required" });

    var results = await query.SearchTextAsync(q, repo, file, limit ?? 20);
    return Results.Ok(results);
}).RequireRateLimiting("search");

// ==================== API Key Management Endpoints ====================

app.MapGet("/api/keys", async (IApiKeyRepository keyRepo) =>
{
    var keys = await keyRepo.ListAsync();
    return Results.Ok(keys.Select(k => new ApiKeyResponse
    {
        ApiKeyId = k.ApiKeyId,
        Name = k.Name,
        KeyPrefix = k.KeyPrefix,
        Scopes = k.Scopes,
        IsActive = k.IsActive,
        CreatedAt = k.CreatedAt,
        LastUsedAt = k.LastUsedAt,
        ExpiresAt = k.ExpiresAt,
        CreatedBy = k.CreatedBy
    }));
}).RequireRateLimiting("general");

app.MapPost("/api/keys", async (CreateApiKeyRequest request, IApiKeyRepository keyRepo, HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required" });

    if (request.Scopes.Count == 0)
        return Results.BadRequest(new { error = "At least one scope is required" });

    var validScopes = new HashSet<string> { "ingest", "code", "mcp", "admin" };
    if (request.Scopes.Any(s => !validScopes.Contains(s)))
        return Results.BadRequest(new { error = "Invalid scope. Valid: ingest, code, mcp, admin" });

    // Generate raw key
    var rawKey = "aimemory_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    var keyHash = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));
    var keyPrefix = rawKey[8..16]; // first 8 hex chars after prefix

    var apiKey = await keyRepo.CreateAsync(new ApiKey
    {
        Name = request.Name,
        KeyHash = keyHash,
        KeyPrefix = keyPrefix,
        Scopes = request.Scopes,
        ExpiresAt = request.ExpiresAt,
        CreatedBy = httpContext.User.Identity?.Name
    });

    return Results.Created($"/api/keys/{apiKey.ApiKeyId}", new CreateApiKeyResponse
    {
        ApiKeyId = apiKey.ApiKeyId,
        Name = apiKey.Name,
        RawKey = rawKey,
        KeyPrefix = keyPrefix,
        Scopes = apiKey.Scopes
    });
}).RequireRateLimiting("general");

app.MapGet("/api/keys/{id:guid}", async (Guid id, IApiKeyRepository keyRepo) =>
{
    var key = await keyRepo.GetByIdAsync(id);
    if (key == null) return Results.NotFound(new { error = "API key not found" });

    return Results.Ok(new ApiKeyResponse
    {
        ApiKeyId = key.ApiKeyId,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        Scopes = key.Scopes,
        IsActive = key.IsActive,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        ExpiresAt = key.ExpiresAt,
        CreatedBy = key.CreatedBy
    });
}).RequireRateLimiting("general");

app.MapPatch("/api/keys/{id:guid}", async (Guid id, UpdateApiKeyRequest request, IApiKeyRepository keyRepo) =>
{
    var key = await keyRepo.UpdateAsync(id, k =>
    {
        if (request.Name != null) k.Name = request.Name;
        if (request.Scopes != null) k.Scopes = request.Scopes;
        if (request.IsActive.HasValue) k.IsActive = request.IsActive.Value;
    });

    if (key == null) return Results.NotFound(new { error = "API key not found" });

    return Results.Ok(new ApiKeyResponse
    {
        ApiKeyId = key.ApiKeyId,
        Name = key.Name,
        KeyPrefix = key.KeyPrefix,
        Scopes = key.Scopes,
        IsActive = key.IsActive,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        ExpiresAt = key.ExpiresAt,
        CreatedBy = key.CreatedBy
    });
}).RequireRateLimiting("general");

app.MapDelete("/api/keys/{id:guid}", async (Guid id, IApiKeyRepository keyRepo) =>
{
    await keyRepo.DeleteAsync(id);
    return Results.Ok(new { message = "API key deleted" });
}).RequireRateLimiting("general");

// ==================== SPA ====================
// Development: SpaProxy hosting startup (via launchSettings.json) auto-launches Vite
// and redirects the browser to http://localhost:5173. Vite serves the React app and
// proxies /api/* back here. No reverse proxy needed from .NET to Vite.
//
// Production: serve the pre-built React app from wwwroot.
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();

// ==================== Helper Methods ====================

static async Task<int> GetMessageCount(IMessageRepository repo, Guid sessionId)
{
    var messages = await repo.GetBySessionAsync(sessionId, 10000);
    return messages.Count;
}

/// <summary>
/// Looks up an existing project by display name, or creates a fresh one with project_id
/// derived from the source path via <see cref="IProjectIdResolver"/>. Used by the legacy
/// ingest path (which carries display name + source path, not project_id).
/// </summary>
static async Task<Project> ResolveOrCreateProjectAsync(
    ICodeIndexRepository repo,
    IProjectIdResolver resolver,
    IHostIdProvider hostIds,
    string displayName,
    string sourceType,
    string sourcePath)
{
    var existing = await repo.GetProjectByNameAsync(displayName);
    if (existing != null) return existing;

    var hostId = hostIds.GetHostId();
    var identity = resolver.Resolve(sourcePath, hostId);

    return await repo.UpsertProjectAsync(new Project
    {
        ProjectId = identity.ProjectId,
        DisplayName = displayName,
        CanonicalRemoteUrl = identity.CanonicalRemoteUrl,
        RootCommitSha = identity.RootCommitSha,
        IdentityKind = identity.IdentityKind,
        SourceType = sourceType,
        SourcePath = sourcePath
    });
}

// ==================== Setup State ====================

public class SetupState
{
    public bool IsSetupComplete { get; set; }
}

// Test hook: WebApplicationFactory<Program> needs a public Program type to bind to.
// Top-level statements compile to an internal Program class; we expose it via a partial
// declaration so the integration tests can host the API in-process. Adding members here
// would change the program semantics — keep this empty.
public partial class Program { }
