# React + ASP.NET Core with Vite in Visual Studio

A practical guide for setting up and running a React (Vite) frontend with an ASP.NET Core backend in Visual Studio. Covers the common pain points so you don't have to debug port mismatches every time.

---

## Architecture Overview

In development there are two processes and one proxy direction:

```
VS launches .NET  -->  SpaProxy hosting startup auto-launches Vite
                       and redirects browser to Vite URL

Browser (localhost:5173)  -->  Vite Dev Server (serves React, hot reloads)
                                |
                                |  proxies /api/* to .NET
                                v
                          ASP.NET Core (localhost:5219)  -->  returns JSON
```

**Key principle: Vite is the entry point in development.** The browser talks to Vite. Vite serves the React app and proxies `/api/*` calls to the .NET backend. The .NET backend only handles API routes — it does NOT reverse-proxy back to Vite.

### What each piece does

| Component | Role |
|---|---|
| **SpaProxy hosting startup** (launchSettings.json) | Auto-launches Vite when .NET starts. Redirects browser from .NET URL to Vite URL. |
| **Vite dev server** (vite.config.ts) | Serves React app with HMR. Proxies `/api/*` to .NET. |
| **ASP.NET Core** (Program.cs) | Handles API endpoints only. In production, also serves the built React app from `wwwroot`. |

### The `UseSpa` / `UseProxyToSpaDevelopmentServer` trap

**Do NOT use `UseSpa` with `UseProxyToSpaDevelopmentServer` in Program.cs.** This creates a reverse proxy from .NET to Vite that intercepts ALL requests — including `/api/*` routes — before endpoint routing can handle them. This causes:

1. API endpoints return 500 errors (proxied to Vite instead of being handled by .NET)
2. Infinite proxy loops: .NET proxies to Vite, Vite proxies `/api` back to .NET, repeat forever
3. `MapFallbackToFile("index.html")` returns 404 in dev because `wwwroot/index.html` doesn't exist yet

The SpaProxy **hosting startup** (configured in launchSettings.json, not in Program.cs) is all you need. It auto-starts Vite and redirects the browser — no reverse proxy required.

---

## Project Structure

```
MyApp/
  src/
    MyApp.Api/                  # ASP.NET Core project
      Properties/
        launchSettings.json     # Defines ports + SpaProxy hosting startup
      ClientApp/                # React app (embedded in API project)
        src/
        package.json
        vite.config.ts          # Proxy config pointing to .NET
      Program.cs
      MyApp.Api.csproj          # SpaRoot, SpaProxyServerUrl, SpaProxyLaunchCommand
    myapp.client/               # (Optional) Standalone React project (.esproj)
      src/
      package.json
      vite.config.ts
```

**Important**: If you have both `ClientApp/` inside the API project AND a standalone `.esproj` client project, keep their configs in sync. The `.csproj`'s `SpaRoot` property determines which one VS uses.

---

## The Three Files That Must Agree on Ports

### 1. launchSettings.json (defines .NET ports + enables SpaProxy)

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "launchBrowser": true,
      "applicationUrl": "http://localhost:5219",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES": "Microsoft.AspNetCore.SpaProxy"
      }
    },
    "https": {
      "commandName": "Project",
      "launchBrowser": true,
      "applicationUrl": "https://localhost:7290;http://localhost:5219",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES": "Microsoft.AspNetCore.SpaProxy"
      }
    }
  }
}
```

The `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` line is what makes the SpaProxy hosting startup active. It auto-launches Vite and redirects the browser.

### 2. vite.config.ts (Vite port + proxy target)

```ts
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Pick the right backend URL based on environment
const target = process.env.ASPNETCORE_HTTPS_PORT
  ? `https://localhost:${process.env.ASPNETCORE_HTTPS_PORT}`
  : process.env.ASPNETCORE_URLS
    ? process.env.ASPNETCORE_URLS.split(';')[0]
    : 'http://localhost:5219'   // <-- MUST match launchSettings http port

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,                 // <-- Vite's port (must match .csproj SpaProxyServerUrl)
    strictPort: true,
    proxy: {
      '/api': {
        target,                 // <-- forwards to .NET backend
        changeOrigin: true,
        secure: false           // allow self-signed certs in dev
      }
    }
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true
  }
})
```

### 3. .csproj (SPA proxy settings)

```xml
<PropertyGroup>
  <SpaRoot>ClientApp\</SpaRoot>
  <SpaProxyServerUrl>http://localhost:5173</SpaProxyServerUrl>  <!-- MUST match Vite port -->
  <SpaProxyLaunchCommand>npm run dev</SpaProxyLaunchCommand>    <!-- script that starts Vite -->
</PropertyGroup>
```

### Program.cs — keep it simple

```csharp
// At the end, before app.Run():

// Production only — serve built React app from wwwroot.
// In development, SpaProxy redirects browser to Vite; no reverse proxy needed.
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();
```

**That's it.** No `UseSpa()`, no `UseProxyToSpaDevelopmentServer()`. The SpaProxy hosting startup handles development. `MapFallbackToFile` handles production.

---

## Port Agreement Checklist

| Setting | Value | Where |
|---|---|---|
| .NET HTTP port | `5219` | launchSettings.json `applicationUrl` |
| .NET HTTPS port | `7290` | launchSettings.json `applicationUrl` |
| Vite dev server port | `5173` | vite.config.ts `server.port` |
| Vite proxy target | `http://localhost:5219` | vite.config.ts `proxy.target` fallback |
| SpaProxyServerUrl | `http://localhost:5173` | .csproj |
| SpaProxyLaunchCommand | `npm run dev` | .csproj |

**Rule**: Vite points at .NET's port (proxy target). .csproj points at Vite's port (SpaProxyServerUrl). If these cross-references don't match, you get "Loading..." forever or connection refused errors.

---

## Running in Development

### Option A: Visual Studio (F5) — recommended

1. Set `MyApp.Api` as the startup project
2. Press F5 (or Ctrl+F5 for no debugger)
3. .NET starts → SpaProxy auto-runs `npm run dev` → Vite starts on :5173
4. Browser opens to the .NET URL (e.g., `https://localhost:7290`)
5. SpaProxy redirects browser to `http://localhost:5173`
6. React app loads, API calls proxy through Vite to .NET

**Pros**: Single F5 starts everything. Debugging .NET is easy.

### Option B: Two terminals

Terminal 1 — start .NET:
```bash
cd src/MyApp.Api
dotnet run --launch-profile http
```

Terminal 2 — start Vite:
```bash
cd src/MyApp.Api/ClientApp    # or src/myapp.client
npm run dev
```

Open `http://localhost:5173` in your browser.

**Pros**: You see errors from both processes. Easy to restart one without the other.
**Cons**: Two terminals to manage.

---

## Common Issues and Fixes

### "Loading..." forever / API calls hang

**Cause 1**: Vite's proxy target doesn't match where .NET is actually running.
**Fix**: Check vite.config.ts proxy target vs launchSettings.json. If using the `http` profile, the target must be `http://localhost:5219`, not `https://localhost:7290`.

**Cause 2**: `UseSpa` + `UseProxyToSpaDevelopmentServer` in Program.cs intercepting API routes.
**Fix**: Remove them entirely. Use the pattern from the "Program.cs — keep it simple" section above.

**Cause 3**: Database config errors crashing the backend on startup (e.g., SQLite provider with a PostgreSQL connection string, or no database configured at all).
**Fix**: Default to SQLite when no database is configured. Wrap migration in try/catch:
```csharp
try { db.Database.Migrate(); }
catch (Exception ex) { logger.Warn(ex, "Migration failed — setup wizard will handle it"); }
```

**Cause 4**: No `appsettings.json` database config on first run but code defaults to PostgreSQL.
**Fix**: Don't hardcode a default database provider or connection string. Let the app start with SQLite and let your setup wizard handle configuration.

### "This localhost page can't be found" (404 on .NET URL)

**Cause**: `MapFallbackToFile("index.html")` runs in development but `wwwroot/index.html` doesn't exist (only created during `dotnet publish`).
**Fix**: Only use `MapFallbackToFile` in production:
```csharp
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}
```

### SpaProxy doesn't start Vite

**Cause**: `SpaProxyLaunchCommand` doesn't match a real npm script (e.g., `npm start` but no `start` script).
**Fix**: Ensure package.json has the script your .csproj references:
```json
"scripts": {
  "dev": "vite",
  "start": "vite",
  "build": "tsc -b && vite build"
}
```

### Infinite proxy loop (requests bounce between .NET and Vite)

**Cause**: `UseProxyToSpaDevelopmentServer` in Program.cs proxies ALL unmatched requests (including `/api/*`) to Vite. Vite sees `/api/*` and proxies them back to .NET. Loop.
**Fix**: Remove `UseSpa` and `UseProxyToSpaDevelopmentServer` from Program.cs entirely. The SpaProxy hosting startup handles dev mode.

### CORS errors

**Cause**: Accessing .NET directly from a page served by Vite (different origins).
**Fix**: Always go through Vite's proxy. Access `localhost:5173` — Vite proxies `/api/*` to .NET on the same origin. No CORS needed.

### Port already in use

**Cause**: Previous dev server didn't shut down cleanly.

**Fix (Windows)**:
```bash
# Find what's using the port
netstat -ano | findstr :5173
# Kill it
taskkill /PID <pid> /F
```

### Two copies of the client app out of sync

**Cause**: VS template creates `ClientApp/` inside the API project, but you may also have a standalone `.esproj`.
**Fix**: Keep configs (vite.config.ts, package.json) identical in both. The `.csproj` `SpaRoot` controls which one the SpaProxy uses.

---

## New Project Setup from Scratch

### 1. Create the ASP.NET Core project

```bash
dotnet new webapi -n MyApp.Api -o src/MyApp.Api
```

### 2. Create the React app with Vite

```bash
cd src/MyApp.Api
npm create vite@latest ClientApp -- --template react-ts
cd ClientApp
npm install
```

### 3. Add SPA proxy package to .NET

```bash
cd src/MyApp.Api
dotnet add package Microsoft.AspNetCore.SpaProxy
```

Note: You do NOT need `Microsoft.AspNetCore.SpaServices.Extensions`. That package provides `UseSpa` / `UseProxyToSpaDevelopmentServer` which you should not use.

### 4. Configure .csproj

Add to `<PropertyGroup>`:
```xml
<SpaRoot>ClientApp\</SpaRoot>
<SpaProxyServerUrl>http://localhost:5173</SpaProxyServerUrl>
<SpaProxyLaunchCommand>npm run dev</SpaProxyLaunchCommand>
```

### 5. Configure launchSettings.json

Add `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` to enable SpaProxy hosting startup:
```json
"environmentVariables": {
  "ASPNETCORE_ENVIRONMENT": "Development",
  "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES": "Microsoft.AspNetCore.SpaProxy"
}
```

### 6. Configure vite.config.ts

Use the template from the "Three Files" section above.

### 7. Configure Program.cs

```csharp
// At the end, before app.Run()
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();
```

### 8. Add build integration to .csproj

```xml
<Target Name="DebugEnsureNodeEnv" BeforeTargets="Build"
        Condition=" '$(Configuration)' == 'Debug' And !Exists('$(SpaRoot)node_modules') ">
  <Exec Command="node --version" ContinueOnError="true">
    <Output TaskParameter="ExitCode" PropertyName="ErrorCode" />
  </Exec>
  <Error Condition="'$(ErrorCode)' != '0'"
         Text="Node.js is required. Install from https://nodejs.org/" />
  <Exec WorkingDirectory="$(SpaRoot)" Command="npm install" />
</Target>

<Target Name="PublishRunWebpack" AfterTargets="ComputeFilesToPublish">
  <Exec WorkingDirectory="$(SpaRoot)" Command="npm install" />
  <Exec WorkingDirectory="$(SpaRoot)" Command="npm run build" />
  <ItemGroup>
    <DistFiles Include="$(SpaRoot)dist\**" />
    <ResolvedFileToPublish Include="@(DistFiles->'%(FullPath)')" Exclude="@(ResolvedFileToPublish)">
      <RelativePath>wwwroot\%(RecursiveDir)%(FileName)%(Extension)</RelativePath>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </ResolvedFileToPublish>
  </ItemGroup>
</Target>
```

---

## Production Build

```bash
cd src/MyApp.Api
dotnet publish -c Release
```

This runs `npm run build`, copies the Vite output into `wwwroot/`, and the .NET app serves it as static files via `MapFallbackToFile("index.html")`.

---

## Quick Reference

```
# Option A: Visual Studio
# Just press F5 — SpaProxy handles everything

# Option B: Two terminals
cd src/MyApp.Api && dotnet run --launch-profile http
cd src/MyApp.Api/ClientApp && npm run dev

# Open browser to http://localhost:5173
```
