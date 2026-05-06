using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AIMemory.Api.Middleware;
using AIMemory.Data.Repositories;
using AIMemory.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AIMemory.Tests.Unit;

/// <summary>
/// Tests for ApiKeyAuthMiddleware scope mapping and path-skipping logic.
/// Each test drives the middleware by calling InvokeAsync with a crafted HttpContext.
/// </summary>
public class ApiKeyAuthMiddlewareTests
{
    // Builds the middleware wired to a mock repo that returns a given ApiKey (or null)
    private static (ApiKeyAuthMiddleware middleware, List<string> invokedPaths) BuildMiddleware(
        ApiKey? dbKey = null,
        string? legacyApiKey = null)
    {
        var invokedPaths = new List<string>();

        RequestDelegate next = ctx =>
        {
            invokedPaths.Add(ctx.Request.Path.Value ?? "");
            return Task.CompletedTask;
        };

        var repoMock = new Mock<IApiKeyRepository>();
        repoMock
            .Setup(r => r.GetByHashAsync(It.IsAny<string>()))
            .ReturnsAsync(dbKey);
        repoMock
            .Setup(r => r.UpdateLastUsedAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(repoMock.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["AIMemory:ApiKey"]).Returns(legacyApiKey);

        var middleware = new ApiKeyAuthMiddleware(
            next,
            configMock.Object,
            NullLogger<ApiKeyAuthMiddleware>.Instance,
            scopeFactory);

        return (middleware, invokedPaths);
    }

    private static HttpContext MakeContext(string path, string? apiKey = null, bool isAuthenticated = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        if (apiKey != null)
            ctx.Request.Headers["X-API-Key"] = apiKey;

        if (isAuthenticated)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "cookie");
            ctx.User = new ClaimsPrincipal(identity);
        }

        return ctx;
    }

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    // ===== Path-skipping (no auth required) =====

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/health/live")]
    public async Task InvokeAsync_HealthPath_CallsNextWithoutAuth(string path)
    {
        var (middleware, invoked) = BuildMiddleware();
        var ctx = MakeContext(path);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/setup")]
    [InlineData("/api/setup/status")]
    public async Task InvokeAsync_SetupPath_CallsNextWithoutAuth(string path)
    {
        var (middleware, invoked) = BuildMiddleware();
        var ctx = MakeContext(path);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    public async Task InvokeAsync_AuthLoginPath_CallsNextWithoutAuth(string path)
    {
        var (middleware, invoked) = BuildMiddleware();
        var ctx = MakeContext(path);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/main.js")]
    public async Task InvokeAsync_NonApiPath_CallsNextWithoutAuth(string path)
    {
        var (middleware, invoked) = BuildMiddleware();
        var ctx = MakeContext(path);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
    }

    // ===== Cookie-authenticated user passes through =====

    [Theory]
    [InlineData("/api/ingest")]
    [InlineData("/api/code")]
    [InlineData("/api/keys")]
    public async Task InvokeAsync_CookieAuthenticatedUser_PassesThroughWithoutApiKey(string path)
    {
        var (middleware, invoked) = BuildMiddleware();
        var ctx = MakeContext(path, isAuthenticated: true);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
    }

    // ===== No API key provided → 401 =====

    [Theory]
    [InlineData("/api/ingest")]
    [InlineData("/api/code")]
    [InlineData("/api/keys")]
    public async Task InvokeAsync_NoApiKey_Returns401(string path)
    {
        var (middleware, _) = BuildMiddleware();
        var ctx = MakeContext(path);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    // ===== Legacy env-var key acts as admin and passes all paths =====

    [Theory]
    [InlineData("/api/ingest")]
    [InlineData("/api/code")]
    [InlineData("/api/keys")]
    public async Task InvokeAsync_LegacyApiKey_AllowsAllProtectedPaths(string path)
    {
        const string legacyKey = "super-secret-legacy-key";
        var (middleware, invoked) = BuildMiddleware(legacyApiKey: legacyKey);
        var ctx = MakeContext(path, apiKey: legacyKey);

        await middleware.InvokeAsync(ctx);

        Assert.Contains(path, invoked);
    }

    // ===== Scope requirements: /api/ingest =====

    [Theory]
    [InlineData("ingest")]
    [InlineData("code")]
    [InlineData("admin")]
    public async Task InvokeAsync_IngestPath_AllowedWithCorrectScopes(string scope)
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("mykey"),
            KeyPrefix = "ak_",
            Name = "test",
            Scopes = [scope],
            IsActive = true
        };

        var (middleware, invoked) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/ingest", apiKey: "mykey");

        await middleware.InvokeAsync(ctx);

        Assert.Contains("/api/ingest", invoked);
    }

    [Fact]
    public async Task InvokeAsync_IngestPath_Returns403WithMcpScope()
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("mcpkey"),
            KeyPrefix = "ak_",
            Name = "mcp-only",
            Scopes = ["mcp"],
            IsActive = true
        };

        var (middleware, _) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/ingest", apiKey: "mcpkey");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    // ===== Scope requirements: /api/code =====

    [Theory]
    [InlineData("code")]
    [InlineData("mcp")]
    [InlineData("admin")]
    public async Task InvokeAsync_CodePath_AllowedWithCorrectScopes(string scope)
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("codekey-" + scope),
            KeyPrefix = "ak_",
            Name = "code-key",
            Scopes = [scope],
            IsActive = true
        };

        var (middleware, invoked) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/code", apiKey: "codekey-" + scope);

        await middleware.InvokeAsync(ctx);

        Assert.Contains("/api/code", invoked);
    }

    [Fact]
    public async Task InvokeAsync_CodePath_Returns403WithIngestOnlyScope()
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("ingest-only"),
            KeyPrefix = "ak_",
            Name = "ingest-only",
            Scopes = ["ingest"],
            IsActive = true
        };

        var (middleware, _) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/code", apiKey: "ingest-only");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    // ===== Scope requirements: /api/keys =====

    [Fact]
    public async Task InvokeAsync_KeysPath_AllowedWithAdminScope()
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("adminkey"),
            KeyPrefix = "ak_",
            Name = "admin-key",
            Scopes = ["admin"],
            IsActive = true
        };

        var (middleware, invoked) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/keys", apiKey: "adminkey");

        await middleware.InvokeAsync(ctx);

        Assert.Contains("/api/keys", invoked);
    }

    [Theory]
    [InlineData("ingest")]
    [InlineData("code")]
    [InlineData("mcp")]
    public async Task InvokeAsync_KeysPath_Returns403WithNonAdminScope(string scope)
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("nonadmin-" + scope),
            KeyPrefix = "ak_",
            Name = "non-admin",
            Scopes = [scope],
            IsActive = true
        };

        var (middleware, _) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/keys", apiKey: "nonadmin-" + scope);

        await middleware.InvokeAsync(ctx);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    // ===== Inactive key is rejected =====

    [Fact]
    public async Task InvokeAsync_InactiveKey_Returns401()
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("inactivekey"),
            KeyPrefix = "ak_",
            Name = "inactive",
            Scopes = ["admin"],
            IsActive = false
        };

        var (middleware, _) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/ingest", apiKey: "inactivekey");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    // ===== Expired key is rejected =====

    [Fact]
    public async Task InvokeAsync_ExpiredKey_Returns401()
    {
        var key = new ApiKey
        {
            ApiKeyId = Guid.NewGuid(),
            KeyHash = Sha256("expiredkey"),
            KeyPrefix = "ak_",
            Name = "expired",
            Scopes = ["admin"],
            IsActive = true,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var (middleware, _) = BuildMiddleware(dbKey: key);
        var ctx = MakeContext("/api/ingest", apiKey: "expiredkey");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    // ===== Unknown key returns 401 =====

    [Fact]
    public async Task InvokeAsync_UnknownApiKey_Returns401()
    {
        // repo returns null (no matching key)
        var (middleware, _) = BuildMiddleware(dbKey: null);
        var ctx = MakeContext("/api/ingest", apiKey: "unknown-key-xyz");

        await middleware.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }
}
