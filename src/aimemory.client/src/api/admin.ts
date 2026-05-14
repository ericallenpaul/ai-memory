/**
 * Admin API helpers — replaces the Tauri shell's distributed/services/network-interface/
 * folder-picker invokes with same-origin fetch calls. Cookie auth is carried by the API
 * helper provided through ApiProvider; these helpers are pure type wrappers so callers
 * never have to remember the path shape.
 */

/* -------------------- Distributed-mode admin -------------------- */

export interface DistributedStatus {
  enabled: boolean;
  bindAddress: string;
  bindPort: number;
  endpoint: string | null;
  fingerprint: string | null;
  pairedHostCount: number;
}

export interface DistributedEnableResponse {
  endpoint: string;
  fingerprint: string;
  apiKey: string;
  restartRequired: boolean;
}

export interface DistributedDisableResponse {
  message: string;
  restartRequired: boolean;
}

export interface PairingRow {
  pairingId: string;
  hostId: string;
  friendlyName: string;
  pairedAt: string;
  lastContactAt: string | null;
  isRevoked: boolean;
}

export interface NetworkInterface {
  name: string;
  address: string;
}

type Fetcher = (path: string, init?: RequestInit) => Promise<Response>;

async function asJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(body.error ?? res.statusText);
  }
  return res.json();
}

export function distributedStatus(api: Fetcher): Promise<DistributedStatus> {
  return api("/api/admin/distributed/status").then(asJson<DistributedStatus>);
}

export function distributedEnable(api: Fetcher, bindInterface?: string): Promise<DistributedEnableResponse> {
  const qs = bindInterface ? `?bindInterface=${encodeURIComponent(bindInterface)}` : "";
  return api(`/api/admin/distributed/enable${qs}`, { method: "POST" }).then(asJson<DistributedEnableResponse>);
}

export function distributedDisable(api: Fetcher): Promise<DistributedDisableResponse> {
  return api("/api/admin/distributed/disable", { method: "POST" }).then(asJson<DistributedDisableResponse>);
}

export function pairingsList(api: Fetcher): Promise<PairingRow[]> {
  return api("/api/pairings").then(asJson<PairingRow[]>);
}

export function pairingsRevoke(api: Fetcher, id: string): Promise<void> {
  return api(`/api/pairings/${encodeURIComponent(id)}`, { method: "DELETE" }).then(async (res) => {
    if (!res.ok) {
      const body = await res.json().catch(() => ({ error: res.statusText }));
      throw new Error(body.error ?? res.statusText);
    }
  });
}

export function listNetworkInterfaces(api: Fetcher): Promise<NetworkInterface[]> {
  return api("/api/admin/network-interfaces").then(asJson<NetworkInterface[]>);
}

/**
 * Format a lowercase-hex SHA-256 fingerprint as colon-separated uppercase byte pairs.
 * Carried over from apps/desktop/src/api/distributed.ts so the Distributed page renders
 * identical text without further surgery.
 */
export function formatFingerprint(hex: string): string {
  if (!hex) return "";
  const clean = hex.replace(/[^0-9a-fA-F]/g, "").toUpperCase();
  const pairs: string[] = [];
  for (let i = 0; i + 2 <= clean.length; i += 2) {
    pairs.push(clean.substring(i, i + 2));
  }
  return pairs.join(":");
}

/* -------------------- Service control -------------------- */

export type ServiceState = "Running" | "Stopped" | "StartPending" | "StopPending" | "NotInstalled" | "Unknown";

export interface ServiceStatusResponse {
  name: string;
  state: ServiceState;
  pid: number | null;
  displayName: string | null;
}

export function serviceStatus(api: Fetcher, name: string): Promise<ServiceStatusResponse> {
  return api(`/api/admin/services/${encodeURIComponent(name)}/status`).then(asJson<ServiceStatusResponse>);
}

export async function serviceCommand(api: Fetcher, name: string, verb: "start" | "stop" | "restart"): Promise<void> {
  const res = await api(`/api/admin/services/${encodeURIComponent(name)}/${verb}`, { method: "POST" });
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(body.error ?? body.detail ?? res.statusText);
  }
}

/* -------------------- Filesystem path validation -------------------- */

export interface ValidatePathResponse {
  path: string;
  exists: boolean;
  isDirectory: boolean;
  isGitRepo: boolean;
}

export function validatePath(api: Fetcher, path: string): Promise<ValidatePathResponse> {
  return api("/api/admin/fs/validate-path", {
    method: "POST",
    body: JSON.stringify({ path }),
  }).then(asJson<ValidatePathResponse>);
}
