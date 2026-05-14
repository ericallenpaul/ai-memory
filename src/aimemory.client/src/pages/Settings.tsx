import { useEffect, useState } from "react";
import { useApi } from "../api/ApiContext";
import { useTheme } from "../ThemeContext";
import { distributedStatus, formatFingerprint, type DistributedStatus } from "../api/admin";

export default function Settings() {
  const { api } = useApi();
  const { theme, toggleTheme } = useTheme();
  const [dist, setDist] = useState<DistributedStatus | null>(null);
  const [distError, setDistError] = useState<string | null>(null);

  useEffect(() => {
    distributedStatus(api).then(setDist, (e) => setDistError(e instanceof Error ? e.message : String(e)));
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div>
      <h1>Settings</h1>

      <div className="card">
        <h2>Appearance</h2>
        <p style={{ color: "var(--text-dim)", fontSize: 13 }}>
          Currently <strong>{theme}</strong>. Preference is stored in localStorage as
          <code className="mono"> aimemory-theme</code>.
        </p>
        <div className="toolbar" style={{ marginBottom: 0 }}>
          <button className="btn secondary" onClick={toggleTheme}>
            Switch to {theme === "dark" ? "light" : "dark"}
          </button>
        </div>
      </div>

      <div className="card">
        <h2>API endpoint</h2>
        <p style={{ color: "var(--text-dim)", fontSize: 13 }}>
          The web UI is served by the API itself, so the API base URL is wherever this page
          loaded from. To change ports, edit <code className="mono">AIMemory:Port</code> in
          <code className="mono"> %ProgramData%\AIMemory\Api\appsettings.json</code> and
          restart the aimemory-api service.
        </p>
        <table>
          <tbody>
            <tr><th>Origin</th><td className="mono">{window.location.origin}</td></tr>
          </tbody>
        </table>
      </div>

      <div className="card">
        <h2>Distributed listener</h2>
        {distError && <div className="error-banner">{distError}</div>}
        {!dist && !distError && <div>Loading…</div>}
        {dist && (
          <table>
            <tbody>
              <tr><th>State</th><td>{dist.enabled ? "Enabled" : "Disabled"}</td></tr>
              <tr><th>Bind</th><td className="mono">{dist.bindAddress}:{dist.bindPort}</td></tr>
              {dist.enabled && dist.endpoint && (
                <tr><th>Endpoint</th><td className="mono">{dist.endpoint}</td></tr>
              )}
              {dist.enabled && dist.fingerprint && (
                <tr>
                  <th>TLS fingerprint</th>
                  <td className="mono" style={{ wordBreak: "break-all" }}>
                    {formatFingerprint(dist.fingerprint)}
                  </td>
                </tr>
              )}
              <tr><th>Paired hosts</th><td>{dist.pairedHostCount}</td></tr>
            </tbody>
          </table>
        )}
        <p style={{ color: "var(--text-dim)", fontSize: 13, marginTop: 8 }}>
          Manage pairings on the <a href="#/distributed">Distributed</a> page.
        </p>
      </div>

      <div className="card">
        <h2>About</h2>
        <p style={{ color: "var(--text-dim)" }}>
          AIMemory · LLM work ledger + code index, served as a single web app on this machine.
        </p>
      </div>
    </div>
  );
}
