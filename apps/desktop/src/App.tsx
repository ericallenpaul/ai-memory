import { useEffect, useState } from "react";
import { BrowserRouter, Route, Routes, NavLink, Navigate } from "react-router-dom";
import { Repos } from "./pages/Repos";
import { RepoDetail } from "./pages/RepoDetail";
import { DbBrowser } from "./pages/DbBrowser";
import { Services } from "./pages/Services";
import { Settings } from "./pages/Settings";
import { Distributed } from "./pages/Distributed";
import { IngestorSetup } from "./pages/IngestorSetup";
import { IngestorStatus } from "./pages/IngestorStatus";
import { ApiProvider } from "./api/ApiContext";
import { appMode, ingestorConfigGet, type AppMode } from "./api/ingestor";
import { getCurrentWindow } from "@tauri-apps/api/window";
import "./App.css";

/**
 * Top-level shell. Calls `app_mode` once at startup and forks the rendered tree:
 *
 * - **Full** (default) — full dashboard, ApiProvider wraps everything because the local
 *   API is present.
 * - **IngestorOnly** — minimal wizard / status view; the local API is *not* present, so
 *   we deliberately skip ApiProvider to avoid a runtime.json load attempt that would
 *   surface as a perma-error banner.
 *
 * Mode is read once and not refreshed; flipping the marker file requires an app restart,
 * which is fine because phase 10's installer is what writes it.
 */
function App() {
  const [mode, setMode] = useState<AppMode | "loading">("loading");

  useEffect(() => {
    appMode().then(
      (m) => setMode(m),
      // If the app_mode command itself fails (shouldn't, but defensive), assume Full.
      () => setMode("Full")
    );
  }, []);

  // Match the window title to the active mode so the taskbar reads "Ingestor Setup" on
  // secondary machines — small thing, but a clear signal that the binary is running in
  // its stripped-down face.
  useEffect(() => {
    if (mode === "loading") return;
    const title = mode === "IngestorOnly" ? "AIMemory — Ingestor Setup" : "AIMemory Desktop";
    getCurrentWindow().setTitle(title).catch(() => { /* non-fatal */ });
  }, [mode]);

  if (mode === "loading") {
    return <div style={{ padding: 32, color: "#94a3b8" }}>Loading…</div>;
  }

  if (mode === "IngestorOnly") {
    return <IngestorOnlyShell />;
  }

  return (
    <ApiProvider>
      <BrowserRouter>
        <div className="app-shell">
          <Sidebar />
          <main className="app-content">
            <Routes>
              <Route path="/" element={<Repos />} />
              <Route path="/repos/:repoId" element={<RepoDetail />} />
              <Route path="/db" element={<DbBrowser />} />
              <Route path="/services" element={<Services />} />
              <Route path="/distributed" element={<Distributed />} />
              <Route path="/settings" element={<Settings />} />
            </Routes>
          </main>
        </div>
      </BrowserRouter>
    </ApiProvider>
  );
}

/**
 * Stripped shell for ingestor-only installs: no sidebar, no ApiProvider. Renders the
 * wizard at `/setup` and the status page at `/`. We pick which one to land on based on
 * whether `appsettings.json` is present + populated. Falls back to `/setup` on read errors
 * so a misconfigured machine never gets stuck on a status view.
 */
function IngestorOnlyShell() {
  const [initialPath, setInitialPath] = useState<string | null>(null);

  useEffect(() => {
    ingestorConfigGet().then(
      (snap) => setInitialPath(snap.configured ? "/" : "/setup"),
      () => setInitialPath("/setup")
    );
  }, []);

  if (initialPath === null) {
    return <div style={{ padding: 32, color: "#94a3b8" }}>Loading…</div>;
  }

  return (
    <BrowserRouter>
      <main className="app-content" style={{ padding: 32 }}>
        <Routes>
          <Route path="/" element={<IngestorStatus />} />
          <Route path="/setup" element={<IngestorSetup />} />
          {/* Belt-and-braces: any other route bounces to the resolved landing page. */}
          <Route path="*" element={<Navigate to={initialPath} replace />} />
        </Routes>
      </main>
    </BrowserRouter>
  );
}

function Sidebar() {
  return (
    <nav className="sidebar">
      <div className="sidebar-brand">AIMemory</div>
      <ul>
        <li><NavLink to="/" end>Repositories</NavLink></li>
        <li><NavLink to="/db">DB Browser</NavLink></li>
        <li><NavLink to="/services">Services</NavLink></li>
        <li><NavLink to="/distributed">Distributed</NavLink></li>
        <li><NavLink to="/settings">Settings</NavLink></li>
      </ul>
    </nav>
  );
}

export default App;
