using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIMemory.Data.Repositories;

namespace AIMemory.Api.Middleware;

/// <summary>
/// API key authentication. Phase 7a: hard-cut to <c>X-AIMemory-Api-Key</c> header — the
/// legacy <c>X-API-Key</c> header is no longer accepted. The auto-generated runtime key is
/// rotated on next start and the new value is published in <c>runtime.json</c>; consumers
/// re-read it from there.
///
/// <para>Loopback requests (127.0.0.1, ::1) without an API key are still allowed for the
/// existing single-machine ingest path that hasn't yet been migrated to send the header —
/// see <see cref="LoopbackBypassEnabled"/>. Once the local ingestor moves to the new auth
/// model in phase 7b, this bypass can be tightened.</para>
/// </summary>
public class ApiKeyAuthMiddleware
{
    /// <summary>The header name secondaries (and all other clients) must send.</summary>
    public const string HeaderName = "X-AIMemory-Api-Key";

    /// <summary>Loopback requests without a valid API key are allowed through. Preserves the
    /// existing single-machine experience until phase 7b updates the local ingestor.</summary>
    public const bool LoopbackBypassEnabled = true;

    private readonly RequestDelegate _next;
    private readonly string? _legacyApiKey;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, (List<string> Scopes, Guid KeyId, DateTimeOffset CachedAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<string, string[]> ScopeRequirements = new()
    {
        ["/api/ingest"] = ["ingest", "code", "admin"],
        ["/api/code"] = ["code", "mcp", "admin"],
        ["/api/sessions"] = ["mcp", "admin"],
        ["/api/search"] = ["mcp", "admin"],
        ["/api/keys"] = ["admin"],
        ["/api/pairings"] = ["admin"],
        ["/api/admin"] = ["admin"],
    };

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration configuration,
        ILogger<ApiKeyAuthMiddleware> logger, IServiceScopeFactory scopeFactory)
    {
        _next = next;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _legacyApiKey = Environment.GetEnvironmentVariable("AIMEMORY_API_KEY")
            ?? configuration["AIMemory:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        // Skip auth for: health, setup, auth login, static files, SPA routes
        if (path.StartsWithSegments("/api/health") ||
            path.StartsWithSegments("/api/setup") ||
            path.StartsWithSegments("/api/auth/login") ||
            !path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Path 1: Cookie auth (web UI)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Path 2: API key auth — new header only (hard cut from X-API-Key per phase 7a).
        var hasHeader = context.Request.Headers.TryGetValue(HeaderName, out var providedKey)
            && !string.IsNullOrEmpty(providedKey);

        // Loopback fallback: requests from 127.0.0.1 / ::1 without a key are still allowed
        // for backcompat with the single-machine ingestor (phase 7b will tighten this).
        if (!hasHeader && LoopbackBypassEnabled && IsLoopback(context))
        {
            await _next(context);
            return;
        }

        if (!hasHeader)
        {
            Reject(context);
            await context.Response.WriteAsJsonAsync(new { error = "Authentication required" });
            return;
        }

        var key = providedKey.ToString();

        // Legacy env var key — acts as admin scope (env-var fallback for non-desktop deploys).
        if (!string.IsNullOrEmpty(_legacyApiKey) && string.Equals(key, _legacyApiKey, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        // DB-backed key validation
        var keyHash = ComputeSha256(key);

        // Check cache first
        if (_cache.TryGetValue(keyHash, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
        {
            if (HasRequiredScope(path, cached.Scopes))
            {
                _ = Task.Run(() => UpdateLastUsedAsync(cached.KeyId));
                await _next(context);
                return;
            }

            _logger.LogWarning("API key {KeyId} lacks required scope for {Path}", cached.KeyId, path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Insufficient permissions" });
            return;
        }

        // Look up in DB
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        var apiKey = await repo.GetByHashAsync(keyHash);

        if (apiKey == null || !apiKey.IsActive ||
            (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTimeOffset.UtcNow))
        {
            Reject(context);
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired API key" });
            return;
        }

        // Cache valid key
        _cache[keyHash] = (apiKey.Scopes, apiKey.ApiKeyId, DateTimeOffset.UtcNow);

        if (!HasRequiredScope(path, apiKey.Scopes))
        {
            _logger.LogWarning("API key {KeyId} lacks required scope for {Path}", apiKey.ApiKeyId, path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Insufficient permissions" });
            return;
        }

        _ = Task.Run(() => UpdateLastUsedAsync(apiKey.ApiKeyId));
        await _next(context);
    }

    private bool HasRequiredScope(PathString path, List<string> scopes)
    {
        foreach (var (pathPrefix, requiredScopes) in ScopeRequirements)
        {
            if (path.StartsWithSegments(pathPrefix))
                return scopes.Any(s => requiredScopes.Contains(s));
        }

        // For other /api/* paths (stats, ingestion-log, etc.), any valid key works
        return true;
    }

    private static bool IsLoopback(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        // IPv4-mapped IPv6 loopback (::ffff:127.0.0.1) maps back to IPv4 loopback.
        if (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4())) return true;
        return false;
    }

    private async Task UpdateLastUsedAsync(Guid keyId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
            await repo.UpdateLastUsedAsync(keyId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update LastUsedAt for key {KeyId}", keyId);
        }
    }

    private void Reject(HttpContext context)
    {
        _logger.LogWarning("Auth failed for {Method} {Path} from {RemoteIp}",
            context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = 401;
        // RFC 7235 — clients SHOULD know which scheme to use.
        if (!context.Response.Headers.ContainsKey("WWW-Authenticate"))
        {
            context.Response.Headers["WWW-Authenticate"] = $"ApiKey realm=\"AIMemory\", header=\"{HeaderName}\"";
        }
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
