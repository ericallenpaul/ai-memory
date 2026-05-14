# aimemory.client

React + Vite SPA for AIMemory, served by `AIMemory.Api` in production (built into
`wwwroot`) and proxied via SpaProxy in development.

## Source-of-truth status

This is the active web client. The `AIMemory.Api.csproj` `<SpaRoot>` points here
(`..\aimemory.client\`) and the publish target builds `dist/` into the API's
`wwwroot/`.

## Pages

* User-facing — `Dashboard, Sessions, SessionDetail, Search, Logs, ApiKeys, SetupWizard, Login`
* Admin — `Repos, RepoDetail, Services, Distributed, DbBrowser, Settings`

The admin pages were ported from the now-removed Tauri shell. They call the
`/api/admin/*` endpoints over same-origin cookie auth (no API key required —
`ApiKeyAuthMiddleware` honors authenticated cookies for admin scope).

## Running

* Dev: `npm run dev` (Vite on http://localhost:5173 with `/api/*` proxied to Kestrel).
  Visual Studio's SpaProxy hosting startup launches Vite automatically when you
  start the API.
* Build: `npm run build` (TypeScript check + Vite build to `dist/`). The API's
  `PublishRunWebpack` target runs this during publish.
