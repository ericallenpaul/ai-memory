import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ingestorPair,
  ingestorTestConnection,
  hostIdGet,
  describePairError,
  type PairArgs,
} from "../api/ingestor";
import { formatFingerprint } from "../api/distributed";

/**
 * Phase 9 — secondary-machine pairing wizard.
 *
 * Runs in ingestor-only mode (and optionally on demand from the status page via "Re-pair").
 * The user pastes the three values from the primary's Distributed page; we run a TLS
 * fingerprint check, an authenticated `/api/health` probe, and finally `POST /api/pairings`.
 * On success we persist `appsettings.json` and bounce to the status view.
 */
export function IngestorSetup() {
  const navigate = useNavigate();

  const [endpoint, setEndpoint] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [keyVisible, setKeyVisible] = useState(false);
  const [fingerprint, setFingerprint] = useState("");
  const [friendlyName, setFriendlyName] = useState("");

  const [hostId, setHostId] = useState<string | null>(null);
  const [hostIdError, setHostIdError] = useState<string | null>(null);

  const [busy, setBusy] = useState<"test" | "pair" | null>(null);
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [pairError, setPairError] = useState<string | null>(null);

  // Pull the host_id once on mount so the user can see "this machine will register as ..."
  // before clicking Pair. Failure is non-fatal — they'll get a clearer error from the
  // pair flow itself if the ingestor exe isn't installed.
  useEffect(() => {
    let cancelled = false;
    hostIdGet().then(
      (h) => { if (!cancelled) setHostId(h); },
      (e) => { if (!cancelled) setHostIdError(describePairError(e)); }
    );
    return () => { cancelled = true; };
  }, []);

  const fingerprintPreview = useMemo(() => {
    const cleaned = fingerprint.replace(/[^0-9a-fA-F]/g, "");
    if (cleaned.length !== 64) return null;
    return formatFingerprint(cleaned);
  }, [fingerprint]);

  const formValid = endpoint.trim().length > 0
    && apiKey.trim().length > 0
    && fingerprintPreview !== null;

  const buildArgs = (): PairArgs => ({
    endpoint: endpoint.trim(),
    apiKey: apiKey.trim(),
    fingerprint: fingerprint.trim(),
    friendlyName: friendlyName.trim() || undefined,
  });

  const handleTest = async () => {
    setBusy("test");
    setTestResult(null);
    setPairError(null);
    try {
      await ingestorTestConnection(buildArgs());
      setTestResult({ ok: true, message: "Connection verified. Fingerprint matches and the API key is accepted." });
    } catch (e) {
      setTestResult({ ok: false, message: describePairError(e) });
    } finally {
      setBusy(null);
    }
  };

  const handlePair = async () => {
    setBusy("pair");
    setTestResult(null);
    setPairError(null);
    try {
      await ingestorPair(buildArgs());
      navigate("/", { replace: true });
    } catch (e) {
      setPairError(describePairError(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div style={{ maxWidth: 720, margin: "0 auto" }}>
      <h1>Pair this machine with an AIMemory primary</h1>
      <p style={{ color: "var(--text-dim)" }}>
        This machine runs the AIMemory ingestor in remote mode. It watches local repositories
        and forwards code-index events to a primary AIMemory server over TLS. To pair, you'll
        need three values from the primary's <strong>Settings → Distributed</strong> page.
      </p>

      <div className="card">
        <h2 style={{ marginTop: 0 }}>This machine</h2>
        {hostId ? (
          <p style={{ marginBottom: 0, color: "var(--text-dim)" }}>
            Will register with host id{" "}
            <span className="mono" title={hostId}>
              {hostId.slice(0, 16)}…
            </span>{" "}
            and friendly name{" "}
            <span className="mono">
              {friendlyName.trim() || guessHostname()}
            </span>.
          </p>
        ) : hostIdError ? (
          <div className="error-banner" style={{ marginBottom: 0 }}>
            Could not read host id: {hostIdError}
          </div>
        ) : (
          <p style={{ marginBottom: 0, color: "var(--text-dim)" }}>Resolving host id…</p>
        )}
      </div>

      <div className="card">
        <h2 style={{ marginTop: 0 }}>Pairing credentials</h2>

        <FormRow label="Primary endpoint" hint="https://eric-desktop.lan:5219">
          <input
            type="text"
            value={endpoint}
            onChange={(e) => setEndpoint(e.target.value)}
            placeholder="https://eric-desktop.lan:5219"
            style={{ width: "100%" }}
            spellCheck={false}
          />
        </FormRow>

        <FormRow label="API key" hint="Issued once on the primary's Distributed page; cannot be read again later.">
          <div style={{ display: "flex", gap: 8 }}>
            <input
              type={keyVisible ? "text" : "password"}
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder="aimemory_..."
              style={{ flex: 1 }}
              spellCheck={false}
              autoComplete="off"
            />
            <button
              className="btn secondary"
              type="button"
              onClick={() => setKeyVisible((v) => !v)}
            >
              {keyVisible ? "Hide" : "Reveal"}
            </button>
          </div>
        </FormRow>

        <FormRow
          label="Cert fingerprint (SHA-256)"
          hint="Accepts colon-separated bytes (A1:B2:…) or raw hex (a1b2…)."
        >
          <input
            type="text"
            value={fingerprint}
            onChange={(e) => setFingerprint(e.target.value)}
            placeholder="A1:B2:C3:…"
            style={{ width: "100%", fontFamily: "SFMono-Regular, Consolas, monospace" }}
            spellCheck={false}
          />
          {fingerprint.trim().length > 0 && (
            <div style={{ marginTop: 6, fontSize: 12, color: fingerprintPreview ? "var(--text-dim)" : "var(--danger)" }}>
              {fingerprintPreview
                ? <>Parsed: <span className="mono">{fingerprintPreview}</span></>
                : "Not a valid SHA-256 fingerprint (need 64 hex characters)."}
            </div>
          )}
        </FormRow>

        <FormRow label="Friendly name (optional)" hint="Shown on the primary's paired-hosts table. Defaults to the OS hostname.">
          <input
            type="text"
            value={friendlyName}
            onChange={(e) => setFriendlyName(e.target.value)}
            placeholder={guessHostname()}
            style={{ width: "100%" }}
            spellCheck={false}
          />
        </FormRow>

        <div className="toolbar" style={{ marginBottom: 0, marginTop: 16 }}>
          <button
            className="btn secondary"
            onClick={handleTest}
            disabled={!formValid || busy !== null}
          >
            {busy === "test" ? "Testing…" : "Test connection"}
          </button>
          <button
            className="btn"
            onClick={handlePair}
            disabled={!formValid || busy !== null}
          >
            {busy === "pair" ? "Pairing…" : "Pair"}
          </button>
        </div>

        {testResult && (
          <div
            className={testResult.ok ? "card" : "error-banner"}
            style={testResult.ok
              ? { marginTop: 16, marginBottom: 0, borderColor: "var(--success)", background: "rgba(16, 185, 129, 0.08)", color: "var(--success)" }
              : { marginTop: 16, marginBottom: 0 }}
          >
            {testResult.ok ? "✓ " : ""}{testResult.message}
          </div>
        )}

        {pairError && (
          <div className="error-banner" style={{ marginTop: 16, marginBottom: 0 }}>
            {pairError}
          </div>
        )}
      </div>

      <div className="card" style={{ background: "rgba(245, 158, 11, 0.04)", borderColor: "var(--border)" }}>
        <h2 style={{ marginTop: 0 }}>What happens when I pair?</h2>
        <ol style={{ color: "var(--text-dim)", marginBottom: 0 }}>
          <li>The wizard opens a TLS connection to the primary and verifies the leaf cert's SHA-256 matches the fingerprint above.</li>
          <li>The API key is sent to <span className="mono">/api/health</span> as a sanity check.</li>
          <li>This machine's host id is registered with the primary via <span className="mono">POST /api/pairings</span>.</li>
          <li>The endpoint, key, and pinned fingerprint are written to <span className="mono">%ProgramData%\AIMemory\Ingestor\appsettings.json</span>. The ingestor service picks them up on its next restart.</li>
        </ol>
      </div>
    </div>
  );
}

function FormRow({
  label, hint, children,
}: {
  label: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div style={{ marginBottom: 12 }}>
      <label style={{ display: "block", fontSize: 13, color: "var(--text-dim)", marginBottom: 4 }}>
        {label}
      </label>
      {children}
      {hint && (
        <div style={{ fontSize: 12, color: "var(--text-dim)", marginTop: 4 }}>{hint}</div>
      )}
    </div>
  );
}

function guessHostname(): string {
  // Pure cosmetic placeholder — the Rust side derives the actual fallback from
  // COMPUTERNAME / HOSTNAME. The browser doesn't have access to that, so we just give
  // the user a stand-in label.
  return "this-machine";
}
