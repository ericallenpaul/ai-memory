import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";

interface ServiceStatusResp {
  name: "api" | "ingestor";
  state: "running" | "stopped" | "startpending" | "stoppending" | "notinstalled" | "unknown";
  pid: number | null;
  displayName: string | null;
}

const SERVICES: ("api" | "ingestor")[] = ["api", "ingestor"];

export function Services() {
  const [statuses, setStatuses] = useState<Record<string, ServiceStatusResp | { error: string }>>({});
  const [busy, setBusy] = useState<string | null>(null);

  const refresh = async () => {
    const next: typeof statuses = {};
    for (const name of SERVICES) {
      try {
        next[name] = await invoke<ServiceStatusResp>("service_status", { name });
      } catch (e: unknown) {
        next[name] = { error: typeof e === "string" ? e : JSON.stringify(e) };
      }
    }
    setStatuses(next);
  };

  useEffect(() => {
    refresh();
    const id = setInterval(refresh, 5000); // poll every 5s
    return () => clearInterval(id);
  }, []);

  const action = async (name: "api" | "ingestor", verb: "start" | "stop" | "restart") => {
    setBusy(`${verb}-${name}`);
    try {
      await invoke(`service_${verb}`, { name });
      await refresh();
    } catch (e) {
      alert(`${verb} ${name} failed: ${typeof e === "string" ? e : JSON.stringify(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      <h1>Services</h1>
      <p style={{ color: "var(--text-dim)" }}>
        AIMemory runs as two Windows Services. The installer registers them at install time;
        this page controls their lifecycle without UAC prompts.
      </p>

      {SERVICES.map((name) => {
        const s = statuses[name];
        return (
          <div key={name} className="card">
            <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
              <h2 style={{ flex: 1, marginBottom: 0 }}>
                {name === "api" ? "AIMemory API" : "AIMemory Ingestor"}
                <span className="mono" style={{ color: "var(--text-dim)", marginLeft: 8, fontSize: 13 }}>
                  aimemory-{name}
                </span>
              </h2>
              {s && "state" in s ? (
                <span className={`badge ${badgeClass(s.state)}`}>{s.state}</span>
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
              <button className="btn" onClick={() => action(name, "start")} disabled={busy !== null}>Start</button>
              <button className="btn secondary" onClick={() => action(name, "stop")} disabled={busy !== null}>Stop</button>
              <button className="btn secondary" onClick={() => action(name, "restart")} disabled={busy !== null}>Restart</button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function badgeClass(state: string): string {
  if (state === "running") return "running";
  if (state === "stopped" || state === "notinstalled") return "stopped";
  if (state.endsWith("pending")) return "pending";
  return "unknown";
}
