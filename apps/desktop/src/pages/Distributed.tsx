import { useEffect, useMemo, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import {
  distributedStatus,
  distributedEnable,
  distributedDisable,
  pairingsList,
  pairingsRevoke,
  listNetworkInterfaces,
  formatFingerprint,
  type DistributedStatus,
  type DistributedEnableResponse,
  type PairingRow,
  type NetworkInterface,
} from "../api/distributed";

/**
 * Settings → Distributed.
 *
 * Manages the "Allow remote ingestors" toggle, surfaces the one-time-reveal pairing
 * credentials (endpoint / API key / cert fingerprint), and lists currently paired hosts
 * with a revoke button each. See design doc §4.1 / §4.3 and the phase 8 spec for the UX.
 */
export function Distributed() {
  const [status, setStatus] = useState<DistributedStatus | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [pairings, setPairings] = useState<PairingRow[] | null>(null);
  const [pairingsError, setPairingsError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  // One-time-reveal panel state. Set after a successful enable; cleared when the user
  // dismisses. We deliberately do NOT persist this — the API never returns the raw key
  // again, so closing the panel is the user's signal that they've copied what they need.
  const [revealed, setRevealed] = useState<DistributedEnableResponse | null>(null);
  const [restartPrompt, setRestartPrompt] = useState<boolean>(false);

  // Bind-interface picker state for the enable confirm dialog.
  const [interfaces, setInterfaces] = useState<NetworkInterface[]>([]);
  const [showEnableDialog, setShowEnableDialog] = useState<boolean>(false);
  const [chosenInterface, setChosenInterface] = useState<string>("0.0.0.0");
  const [customInterface, setCustomInterface] = useState<string>("");

  const refresh = async () => {
    try {
      const s = await distributedStatus();
      setStatus(s);
      setStatusError(null);
    } catch (e) {
      setStatusError(errString(e));
    }
    try {
      const p = await pairingsList();
      setPairings(p);
      setPairingsError(null);
    } catch (e) {
      setPairingsError(errString(e));
    }
  };

  useEffect(() => {
    refresh();
    // 7s cadence — the spec calls for 5–10s and the hosts table is the only thing that
    // actually changes underneath us once enabled (last_contact_at on each row).
    const id = setInterval(refresh, 7000);
    return () => clearInterval(id);
  }, []);

  const openEnableDialog = async () => {
    try {
      const list = await listNetworkInterfaces();
      setInterfaces(list);
      // Default to the first non-wildcard suggestion if available, otherwise the wildcard.
      const firstReal = list.find((i) => i.address !== "0.0.0.0");
      setChosenInterface(firstReal?.address ?? "0.0.0.0");
      setCustomInterface("");
    } catch {
      // If interface enumeration fails, the picker still works with just the wildcard.
      setInterfaces([{ name: "Any (0.0.0.0)", address: "0.0.0.0" }]);
      setChosenInterface("0.0.0.0");
    }
    setShowEnableDialog(true);
  };

  const handleEnable = async () => {
    const iface = chosenInterface === "__custom" ? customInterface.trim() : chosenInterface;
    if (chosenInterface === "__custom" && !iface) {
      alert("Enter a custom bind interface or pick one from the list.");
      return;
    }
    setShowEnableDialog(false);
    setBusy("enable");
    try {
      const resp = await distributedEnable(iface || undefined);
      setRevealed(resp);
      setRestartPrompt(resp.restartRequired);
      await refresh();
    } catch (e) {
      alert(`Enable failed: ${errString(e)}`);
    } finally {
      setBusy(null);
    }
  };

  const handleDisable = async () => {
    if (!confirm(
      "Disable remote-ingestor mode? Existing paired hosts will be unreachable until you " +
      "re-enable. The API service will need to restart to apply the change."
    )) return;
    setBusy("disable");
    try {
      const resp = await distributedDisable();
      if (resp.restartRequired) setRestartPrompt(true);
      setRevealed(null);
      await refresh();
    } catch (e) {
      alert(`Disable failed: ${errString(e)}`);
    } finally {
      setBusy(null);
    }
  };

  const handleRestartNow = async () => {
    setBusy("restart");
    try {
      await invoke("service_restart", { name: "api" });
      setRestartPrompt(false);
      // Give Kestrel a moment to come back up before we re-poll.
      await new Promise((r) => setTimeout(r, 2000));
      await refresh();
    } catch (e) {
      alert(`Restart failed: ${errString(e)}`);
    } finally {
      setBusy(null);
    }
  };

  const handleRevoke = async (p: PairingRow) => {
    if (!confirm(
      `Revoke pairing for "${p.friendlyName}"? The remote ingestor will start receiving ` +
      `401 errors and stop sending data until it is re-paired.`
    )) return;
    setBusy(`revoke-${p.pairingId}`);
    try {
      await pairingsRevoke(p.pairingId);
      await refresh();
    } catch (e) {
      alert(`Revoke failed: ${errString(e)}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <div>
      <div className="toolbar">
        <h1 style={{ flex: 1, marginBottom: 0 }}>Distributed</h1>
        <button className="btn secondary" onClick={refresh} disabled={busy !== null}>
          Refresh
        </button>
      </div>

      <p style={{ color: "var(--text-dim)", marginTop: 0 }}>
        Allow ingestors running on other machines to forward indexing events into this
        primary's database. Pair each remote machine once; data is dedup'd by content
        hash and tagged with its host of origin.
      </p>

      {statusError && (
        <div className="error-banner">Could not load distributed status: {statusError}</div>
      )}

      {restartPrompt && (
        <div className="card" style={{ borderColor: "var(--warning)", background: "rgba(245, 158, 11, 0.08)" }}>
          <h2 style={{ marginTop: 0, color: "var(--warning)" }}>Restart required</h2>
          <p style={{ color: "var(--text-dim)" }}>
            The listener configuration changed. The AIMemory API service must restart to
            rebind its socket and start (or stop) presenting the TLS cert.
          </p>
          <div className="toolbar" style={{ marginBottom: 0 }}>
            <button className="btn" onClick={handleRestartNow} disabled={busy !== null}>
              Restart now
            </button>
            <button className="btn secondary" onClick={() => setRestartPrompt(false)} disabled={busy !== null}>
              Restart later
            </button>
          </div>
        </div>
      )}

      <StatusCard
        status={status}
        busy={busy}
        onEnable={openEnableDialog}
        onDisable={handleDisable}
      />

      {revealed && <RevealCard data={revealed} onDismiss={() => setRevealed(null)} />}

      <PairingsCard
        pairings={pairings}
        error={pairingsError}
        busy={busy}
        onRevoke={handleRevoke}
      />

      {showEnableDialog && (
        <EnableDialog
          interfaces={interfaces}
          chosen={chosenInterface}
          custom={customInterface}
          onChange={(v) => setChosenInterface(v)}
          onCustomChange={(v) => setCustomInterface(v)}
          onConfirm={handleEnable}
          onCancel={() => setShowEnableDialog(false)}
        />
      )}
    </div>
  );
}

/* -------------------------------- Status card -------------------------------- */

function StatusCard({
  status, busy, onEnable, onDisable,
}: {
  status: DistributedStatus | null;
  busy: string | null;
  onEnable: () => void;
  onDisable: () => void;
}) {
  if (!status) {
    return <div className="card"><div>Loading status…</div></div>;
  }
  return (
    <div className="card">
      <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
        <h2 style={{ flex: 1, marginBottom: 0 }}>Allow remote ingestors</h2>
        <span className={`badge ${status.enabled ? "running" : "stopped"}`}>
          {status.enabled ? "Enabled" : "Disabled"}
        </span>
      </div>
      {status.enabled ? (
        <p style={{ marginTop: 12, marginBottom: 8, color: "var(--text-dim)" }}>
          Listening on <span className="mono">{status.endpoint}</span>. {" "}
          {status.pairedHostCount === 0
            ? "No remote ingestors paired yet."
            : `${status.pairedHostCount} active pairing${status.pairedHostCount === 1 ? "" : "s"}.`}
        </p>
      ) : (
        <p style={{ marginTop: 12, marginBottom: 8, color: "var(--text-dim)" }}>
          The API is bound to localhost only. Toggle on to expose it on a network interface
          with TLS so remote ingestors can pair.
        </p>
      )}
      <div className="toolbar" style={{ marginBottom: 0 }}>
        {status.enabled ? (
          <button className="btn danger" onClick={onDisable} disabled={busy !== null}>
            Disable
          </button>
        ) : (
          <button className="btn" onClick={onEnable} disabled={busy !== null}>
            Enable…
          </button>
        )}
      </div>
    </div>
  );
}

/* ------------------------------ Reveal card --------------------------------- */

function RevealCard({
  data, onDismiss,
}: {
  data: DistributedEnableResponse;
  onDismiss: () => void;
}) {
  const [keyVisible, setKeyVisible] = useState(false);
  const [fingerprintRaw, setFingerprintRaw] = useState(false);

  const formattedFingerprint = useMemo(() => formatFingerprint(data.fingerprint), [data.fingerprint]);

  return (
    <div className="card" style={{ borderColor: "var(--accent)" }}>
      <h2 style={{ marginTop: 0 }}>Pair a remote ingestor</h2>
      <p style={{ color: "var(--text-dim)" }}>
        Copy these three values into the wizard on the remote machine.{" "}
        <strong>The API key is shown only once</strong> — if you dismiss this card without copying
        it, you'll need to re-issue a new key by toggling distributed mode off and on again.
      </p>

      <RevealRow label="Endpoint" value={data.endpoint} mono />

      <RevealRow
        label="API key"
        value={keyVisible ? data.apiKey : maskKey(data.apiKey)}
        copyValue={data.apiKey}
        mono
        rightAction={{
          label: keyVisible ? "Hide" : "Reveal",
          onClick: () => setKeyVisible((v) => !v),
        }}
      />

      <RevealRow
        label="Cert fingerprint (SHA-256)"
        value={fingerprintRaw ? data.fingerprint : formattedFingerprint}
        copyValue={fingerprintRaw ? data.fingerprint : formattedFingerprint}
        mono
        rightAction={{
          label: fingerprintRaw ? "Show formatted" : "Show raw hex",
          onClick: () => setFingerprintRaw((v) => !v),
        }}
      />

      <div className="toolbar" style={{ marginTop: 16, marginBottom: 0 }}>
        <button className="btn secondary" onClick={onDismiss}>I've copied these — dismiss</button>
      </div>
    </div>
  );
}

function RevealRow({
  label, value, copyValue, mono, rightAction,
}: {
  label: string;
  value: string;
  copyValue?: string;
  mono?: boolean;
  rightAction?: { label: string; onClick: () => void };
}) {
  const [copied, setCopied] = useState(false);
  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(copyValue ?? value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard API can fail in non-secure contexts; surface a soft fallback.
      alert("Copy failed — select the value manually.");
    }
  };
  return (
    <div style={{ marginBottom: 12 }}>
      <div style={{ fontSize: 13, color: "var(--text-dim)", marginBottom: 4 }}>{label}</div>
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        <div
          className={mono ? "mono" : ""}
          style={{
            flex: 1, padding: "8px 12px", background: "var(--bg)",
            border: "1px solid var(--border)", borderRadius: 6, wordBreak: "break-all",
          }}
        >
          {value}
        </div>
        <button className="btn secondary" onClick={handleCopy}>
          {copied ? "Copied!" : "Copy"}
        </button>
        {rightAction && (
          <button className="btn secondary" onClick={rightAction.onClick}>
            {rightAction.label}
          </button>
        )}
      </div>
    </div>
  );
}

function maskKey(raw: string): string {
  if (raw.length <= 12) return "•".repeat(raw.length);
  return `${raw.slice(0, 9)}${"•".repeat(Math.max(0, raw.length - 13))}${raw.slice(-4)}`;
}

/* ----------------------------- Pairings card -------------------------------- */

function PairingsCard({
  pairings, error, busy, onRevoke,
}: {
  pairings: PairingRow[] | null;
  error: string | null;
  busy: string | null;
  onRevoke: (p: PairingRow) => void;
}) {
  return (
    <div className="card">
      <h2 style={{ marginTop: 0 }}>Paired hosts</h2>
      {error && <div className="error-banner">{error}</div>}
      {pairings === null ? (
        <div className="empty-state">Loading…</div>
      ) : pairings.length === 0 ? (
        <div className="empty-state">No remote ingestors paired yet.</div>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Friendly name</th>
              <th>Host ID</th>
              <th>Paired at</th>
              <th>Last contact</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {pairings.map((p) => (
              <tr key={p.pairingId}>
                <td>{p.friendlyName}</td>
                <td className="mono" style={{ color: "var(--text-dim)" }} title={p.hostId}>
                  {p.hostId.slice(0, 12)}…
                </td>
                <td>{new Date(p.pairedAt).toLocaleString()}</td>
                <td style={{ color: "var(--text-dim)" }}>
                  {p.lastContactAt ? new Date(p.lastContactAt).toLocaleString() : "never"}
                </td>
                <td>
                  <button
                    className="btn danger"
                    onClick={() => onRevoke(p)}
                    disabled={busy !== null}
                  >
                    Revoke
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

/* ----------------------------- Enable dialog -------------------------------- */

function EnableDialog({
  interfaces, chosen, custom, onChange, onCustomChange, onConfirm, onCancel,
}: {
  interfaces: NetworkInterface[];
  chosen: string;
  custom: string;
  onChange: (v: string) => void;
  onCustomChange: (v: string) => void;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div
      style={{
        position: "fixed", inset: 0, background: "rgba(0,0,0,0.5)",
        display: "flex", alignItems: "center", justifyContent: "center", zIndex: 50,
      }}
      onClick={onCancel}
    >
      <div
        className="card"
        style={{ maxWidth: 520, width: "90%", margin: 0 }}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 style={{ marginTop: 0 }}>Enable remote ingestors?</h2>
        <p style={{ color: "var(--text-dim)" }}>
          The API will rebind to the chosen interface and start presenting a self-signed TLS
          cert. Pick the network interface remote ingestors will reach. Pasting a custom
          value is fine if your interface isn't auto-detected.
        </p>

        <div style={{ marginBottom: 12 }}>
          <label style={{ display: "block", fontSize: 13, color: "var(--text-dim)", marginBottom: 4 }}>
            Bind interface
          </label>
          <select
            value={chosen}
            onChange={(e) => onChange(e.target.value)}
            style={{ width: "100%" }}
          >
            {interfaces.map((i) => (
              <option key={i.address} value={i.address}>
                {i.name} — {i.address}
              </option>
            ))}
            <option value="__custom">Custom…</option>
          </select>
        </div>

        {chosen === "__custom" && (
          <div style={{ marginBottom: 12 }}>
            <label style={{ display: "block", fontSize: 13, color: "var(--text-dim)", marginBottom: 4 }}>
              Custom address (e.g. <span className="mono">192.168.10.5</span>)
            </label>
            <input
              type="text"
              value={custom}
              onChange={(e) => onCustomChange(e.target.value)}
              placeholder="0.0.0.0"
              style={{ width: "100%" }}
            />
          </div>
        )}

        <div className="error-banner" style={{ background: "rgba(245, 158, 11, 0.08)", borderColor: "var(--warning)", color: "var(--warning)" }}>
          Exposing AIMemory on a public IP without a VPN is unsafe. LAN-only is the
          supported deployment shape.
        </div>

        <div className="toolbar" style={{ marginBottom: 0, marginTop: 16 }}>
          <button className="btn" onClick={onConfirm}>Enable</button>
          <button className="btn secondary" onClick={onCancel}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

/* ------------------------------- helpers ------------------------------------ */

function errString(e: unknown): string {
  if (typeof e === "string") return e;
  if (e instanceof Error) return e.message;
  try { return JSON.stringify(e); } catch { return String(e); }
}
