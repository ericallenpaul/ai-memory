// TypeScript types + invoke helpers for the **secondary-machine** ingestor pairing wizard
// (phase 9). Mirrors the Rust commands in `apps/desktop/src-tauri/src/ingestor.rs` and the
// app-mode detector in `apps/desktop/src-tauri/src/app_mode.rs`.

import { invoke } from "@tauri-apps/api/core";

/* -------------------------- App mode -------------------------- */

export type AppMode = "Full" | "IngestorOnly";

/**
 * Reads `%ProgramData%\AIMemory\app-mode.json`. Missing / unparseable → "Full".
 *
 * The shell uses this at startup to decide whether to render the full dashboard or the
 * stripped wizard-only view. Phase 10's installer writes the marker file on
 * ingestor-only installs.
 */
export function appMode(): Promise<AppMode> {
  return invoke<AppMode>("app_mode");
}

/* -------------------------- Pairing types -------------------------- */

export interface PairArgs {
  endpoint: string;
  /** Raw API key (sent over the wire to the primary). The wizard never persists this in
   *  React state longer than necessary; it lives in Tauri land once `pair` succeeds. */
  apiKey: string;
  /** Either lowercase hex (`a1b2...`) or colon-separated bytes (`A1:B2:...`). The Rust
   *  side normalizes both to lowercase hex with no separators. */
  fingerprint: string;
  /** Optional friendly name override; defaults to the OS hostname server-side. */
  friendlyName?: string;
}

export interface PairingResult {
  pairingId: string;
  hostId: string;
  friendlyName: string;
  pairedAt: string;
}

/**
 * Tagged-error union returned from `ingestor_pair` / `ingestor_test_connection`.
 * The Rust side serializes `PairError` as `{ type: "...", detail: ... }` (serde
 * `#[serde(tag = "type", content = "detail")]`), so this matches that shape.
 */
export type PairError =
  | { type: "FingerprintMismatch"; detail: { expected: string; actual: string } }
  | { type: "Unreachable"; detail: string }
  | { type: "Unauthorized"; detail?: undefined }
  | { type: "InvalidInput"; detail: string }
  | { type: "Io"; detail: string }
  | { type: "Other"; detail: string };

export interface IngestorPairingSnapshot {
  endpoint: string;
  apiKeyMasked: string;
  fingerprint: string;
  configured: boolean;
}

/* -------------------------- invoke wrappers -------------------------- */

/** Performs the TLS pin + auth probe. Does NOT register a pairing or persist anything. */
export function ingestorTestConnection(args: PairArgs): Promise<void> {
  return invoke("ingestor_test_connection", { args });
}

/** End-to-end pair: pin + probe + POST /api/pairings + persist appsettings.json. */
export function ingestorPair(args: PairArgs): Promise<PairingResult> {
  return invoke<PairingResult>("ingestor_pair", { args });
}

/** Reads the persisted ingestor config. Returns a default-empty snapshot when no
 *  pairing is configured yet. The API key is always returned masked. */
export function ingestorConfigGet(): Promise<IngestorPairingSnapshot> {
  return invoke<IngestorPairingSnapshot>("ingestor_config_get");
}

/** Wipes the persisted ingestor config. Does NOT stop the running service. */
export function ingestorConfigClear(): Promise<void> {
  return invoke("ingestor_config_clear");
}

/** Resolves the local machine's stable host_id by shelling out to the ingestor exe. */
export function hostIdGet(): Promise<string> {
  return invoke<string>("host_id_get");
}

/* -------------------------- Helpers -------------------------- */

/**
 * Best-effort decoder for the tagged PairError variant. Tauri marshals errors thrown by
 * `Result<T, E>` commands as `{ type, detail }` JSON; an unrecognized payload falls
 * back to a string description.
 */
export function describePairError(e: unknown): string {
  if (e && typeof e === "object" && "type" in e) {
    const err = e as PairError;
    switch (err.type) {
      case "FingerprintMismatch":
        return `Fingerprint mismatch — primary presented ${formatShortFingerprint(err.detail.actual)} but you pinned ${formatShortFingerprint(err.detail.expected)}. Re-paste the value from the primary's Distributed page or re-issue credentials.`;
      case "Unreachable":
        return `Could not reach the primary: ${err.detail}`;
      case "Unauthorized":
        return "The primary rejected the API key. Re-issue from the Distributed page on the primary and try again.";
      case "InvalidInput":
        return `Invalid input: ${err.detail}`;
      case "Io":
        return `IO error: ${err.detail}`;
      case "Other":
        return err.detail;
    }
  }
  if (typeof e === "string") return e;
  if (e instanceof Error) return e.message;
  try { return JSON.stringify(e); } catch { return String(e); }
}

function formatShortFingerprint(hex: string): string {
  const clean = hex.replace(/[^0-9a-fA-F]/g, "").toUpperCase();
  if (clean.length < 12) return clean;
  return `${clean.slice(0, 6)}…${clean.slice(-6)}`;
}
