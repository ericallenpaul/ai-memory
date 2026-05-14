import { useEffect, useState } from "react";
import { useApi } from "../api/ApiContext";
import {
  serviceStatus, serviceCommand,
  type ServiceState, type ServiceStatusResponse,
} from "../api/admin";

const SERVICES = [
  { fullName: "aimemory-api", label: "AIMemory API" },
  { fullName: "aimemory-ingestor", label: "AIMemory Ingestor" },
] as const;

type Slot = ServiceStatusResponse | { error: string };

export default function Services() {
  const { api } = useApi();
  const [statuses, setStatuses] = useState<Record<string, Slot>>({});
  const [busy, setBusy] = useState<string | null>(null);

  const refresh = async () => {
    const next: Record<string, Slot> = {};
    for (const svc of SERVICES) {
      try {
        next[svc.fullName] = await serviceStatus(api, svc.fullName);
      } catch (e: unknown) {
        next[svc.fullName] = { error: e instanceof Error ? e.message : String(e) };
      }
    }
    setStatuses(next);
  };

  useEffect(() => {
    refresh();
    const id = setInterval(refresh, 5000);
    return () => clearInterval(id);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const action = async (name: string, verb: "start" | "stop" | "restart") => {
    setBusy(`${verb}-${name}`);
    try {
      await serviceCommand(api, name, verb);
      await refresh();
    } catch (e) {
      alert(`${verb} ${name} failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      <h1>Services</h1>
      <p style={{ color: "var(--text-dim)" }}>
        AIMemory runs as two services (Windows: aimemory-api, aimemory-ingestor). The installer
        registers them at install time; this page controls their lifecycle.
      </p>

      {SERVICES.map(({ fullName, label }) => {
        const s = statuses[fullName];
        return (
          <div key={fullName} className="card">
            <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
              <h2 style={{ flex: 1, marginBottom: 0 }}>
                {label}
                <span className="mono" style={{ color: "var(--text-dim)", marginLeft: 8, fontSize: 13 }}>
                  {fullName}
                </span>
              </h2>
              {s && "state" in s ? (
                <span className={`badge ${badgeClass(s.state)}`}>{s.state.toLowerCase()}</span>
              ) : (
                <span className="badge unknown">{s ? "error" : "loading"}</span>
              )}
            </div>

            {s && "state" in s && (
              <div style={{ marginTop: 12, fontSize: 13, color: "var(--text-dim)" }}>
                {s.pid !== null && <>PID: <span className="mono">{s.pid}</span> · </>}
                {s.displayName && <>Display name: {s.displayName}</>}
              </div>
            )}

            {s && "error" in s && <div className="error-banner" style={{ marginTop: 8 }}>{s.error}</div>}

            <div className="toolbar" style={{ marginTop: 12, marginBottom: 0 }}>
              <button className="btn" onClick={() => action(fullName, "start")} disabled={busy !== null}>Start</button>
              <button className="btn secondary" onClick={() => action(fullName, "stop")} disabled={busy !== null}>Stop</button>
              <button className="btn secondary" onClick={() => action(fullName, "restart")} disabled={busy !== null}>Restart</button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function badgeClass(state: ServiceState): string {
  if (state === "Running") return "running";
  if (state === "Stopped" || state === "NotInstalled") return "stopped";
  if (state === "StartPending" || state === "StopPending") return "pending";
  return "unknown";
}
