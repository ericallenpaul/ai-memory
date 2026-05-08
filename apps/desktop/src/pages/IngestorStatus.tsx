import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ingestorConfigGet,
  ingestorConfigClear,
  hostIdGet,
  describePairError,
  type IngestorPairingSnapshot,
} from "../api/ingestor";
import { formatFingerprint } from "../api/distributed";

/**
 * Phase 9 — minimal status view shown after a successful pairing.
 *
 * The wizard ran, `appsettings.json` is on disk, the ingestor service is (or will be)
 * happily talking to the primary. This page is here so the user has a place to land on
 * subsequent launches in ingestor-only mode — and to expose a "Re-pair" button for the
 * cases where credentials were rotated, the cert was rolled, or they're moving primaries.
 *
 * Intentionally minimal in v1: we don't yet surface the ingestor's runtime ingest stats
 * here (no obvious signal exposed by the service). Phase 10+ can add a small NDJSON tail
 * or an outbox-status query.
 */
export function IngestorStatus() {
  const navigate = useNavigate();
  const [snapshot, setSnapshot] = useState<IngestorPairingSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [hostId, setHostId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = async () => {
    try {
      const s = await ingestorConfigGet();
      setSnapshot(s);
      setError(null);
      // If the config was wiped between renders (e.g. user clicked Re-pair), bounce
      // straight to the wizard.
      if (!s.configured) {
        navigate("/setup", { replace: true });
      }
    } catch (e) {
      setError(describePairError(e));
    }
  };

  useEffect(() => {
    refresh();
    hostIdGet().then(setHostId, () => setHostId(null));
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const handleRepair = async () => {
    if (!confirm(
      "Clear the saved pairing and return to the wizard? The ingestor service will start " +
      "failing requests until you complete a new pairing — restart the service after pairing " +
      "to pick up the new config."
    )) return;
    setBusy(true);
    try {
      await ingestorConfigClear();
      navigate("/setup", { replace: true });
    } catch (e) {
      alert(`Could not clear config: ${describePairError(e)}`);
    } finally {
      setBusy(false);
    }
  };

  if (error) {
    return (
      <div style={{ maxWidth: 720, margin: "0 auto" }}>
        <h1>Ingestor status</h1>
        <div className="error-banner">{error}</div>
      </div>
    );
  }

  if (!snapshot) {
    return (
      <div style={{ maxWidth: 720, margin: "0 auto" }}>
        <h1>Ingestor status</h1>
        <p style={{ color: "var(--text-dim)" }}>Loading…</p>
      </div>
    );
  }

  return (
    <div style={{ maxWidth: 720, margin: "0 auto" }}>
      <h1>Ingestor status</h1>

      <div className="card">
        <h2 style={{ marginTop: 0 }}>Paired with</h2>
        <Row label="Endpoint" value={snapshot.endpoint} mono />
        <Row label="API key" value={snapshot.apiKeyMasked} mono />
        <Row
          label="Pinned fingerprint"
          value={formatFingerprint(snapshot.fingerprint)}
          mono
        />
        {hostId && (
          <Row label="This host id" value={hostId} mono small />
        )}
      </div>

      <div className="card">
        <h2 style={{ marginTop: 0 }}>Recent activity</h2>
        <p style={{ color: "var(--text-dim)", marginBottom: 0 }}>
          The ingestor service handles batching, retry, and outbox queueing on its own.
          Inspect <span className="mono">aimemory-ingestor</span> service logs (Event
          Viewer → Application, source <span className="mono">aimemory-ingestor</span>) for
          ingest detail. A first-class activity feed lands in a follow-up phase.
        </p>
      </div>

      <div className="card" style={{ borderColor: "var(--warning)", background: "rgba(245, 158, 11, 0.04)" }}>
        <h2 style={{ marginTop: 0 }}>Re-pair</h2>
        <p style={{ color: "var(--text-dim)" }}>
          Use this if the primary's credentials or cert have been rotated, or you're moving
          this machine to a different primary. The current config is wiped and the wizard
          opens for fresh values.
        </p>
        <div className="toolbar" style={{ marginBottom: 0 }}>
          <button className="btn danger" onClick={handleRepair} disabled={busy}>
            {busy ? "Clearing…" : "Re-pair…"}
          </button>
        </div>
      </div>
    </div>
  );
}

function Row({
  label, value, mono, small,
}: {
  label: string;
  value: string;
  mono?: boolean;
  small?: boolean;
}) {
  return (
    <div style={{ marginBottom: 12 }}>
      <div style={{ fontSize: 13, color: "var(--text-dim)", marginBottom: 4 }}>{label}</div>
      <div
        className={mono ? "mono" : ""}
        style={{
          padding: "8px 12px",
          background: "var(--bg)",
          border: "1px solid var(--border)",
          borderRadius: 6,
          wordBreak: "break-all",
          fontSize: small ? 12 : undefined,
        }}
      >
        {value || <span style={{ color: "var(--text-dim)" }}>—</span>}
      </div>
    </div>
  );
}
