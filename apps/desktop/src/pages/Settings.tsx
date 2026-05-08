import { useApi } from "../api/ApiContext";

export function Settings() {
  const { status, config, error, refetch } = useApi();

  return (
    <div>
      <h1>Settings</h1>

      <div className="card">
        <h2>Runtime</h2>
        <p style={{ color: "var(--text-dim)" }}>
          Read from <code className="mono">%ProgramData%\AIMemory\Api\runtime.json</code>,
          which the API service publishes on startup.
        </p>
        {status === "loading" && <div>Loading…</div>}
        {status === "error" && <div className="error-banner">{error}</div>}
        {status === "ready" && config && (
          <table>
            <tbody>
              <tr><th>Base URL</th><td className="mono">{config.baseUrl}</td></tr>
              <tr><th>Port</th><td className="mono">{config.port}</td></tr>
              <tr><th>Bind interface</th><td className="mono">{config.bind_interface || "127.0.0.1"}</td></tr>
              <tr><th>API key</th><td className="mono">{config.apiKey.slice(0, 16)}…</td></tr>
              {config.tls_fingerprint && (
                <tr><th>TLS fingerprint</th><td className="mono" style={{ wordBreak: "break-all" }}>{config.tls_fingerprint}</td></tr>
              )}
            </tbody>
          </table>
        )}
        <div style={{ marginTop: 12 }}>
          <button className="btn secondary" onClick={refetch}>Reload</button>
        </div>
      </div>

      <div className="card">
        <h2>About</h2>
        <p style={{ color: "var(--text-dim)" }}>
          AIMemory Desktop · v0.1.0 · Tauri shell over a local code-indexer service.
        </p>
      </div>
    </div>
  );
}
