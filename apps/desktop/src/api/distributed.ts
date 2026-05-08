// TypeScript types + invoke helpers for the Distributed-mode admin commands.
//
// The Rust side returns serde_json::Value; we declare the matching shapes here so callers
// stay strongly typed at the page boundary. Field names mirror the API DTOs at
// `src/AIMemory.Models/Dtos/DistributedDtos.cs` and `PairingDtos.cs`.

import { invoke } from "@tauri-apps/api/core";

/** GET /api/admin/distributed/status response. */
export interface DistributedStatus {
  enabled: boolean;
  bindAddress: string;
  bindPort: number;
  endpoint: string | null;
  fingerprint: string | null;
  pairedHostCount: number;
}

/** POST /api/admin/distributed/enable response — credentials are revealed once. */
export interface DistributedEnableResponse {
  endpoint: string;
  fingerprint: string;
  apiKey: string;
  restartRequired: boolean;
}

/** POST /api/admin/distributed/disable response. */
export interface DistributedDisableResponse {
  message: string;
  restartRequired: boolean;
}

/** GET /api/pairings row. */
export interface PairingRow {
  pairingId: string;
  hostId: string;
  friendlyName: string;
  pairedAt: string;
  lastContactAt: string | null;
  isRevoked: boolean;
}

/** Network interface candidate for the bind-interface picker. */
export interface NetworkInterface {
  name: string;
  address: string;
}

/* -------------------------- invoke wrappers -------------------------- */

export function distributedStatus(): Promise<DistributedStatus> {
  return invoke<DistributedStatus>("distributed_status");
}

export function distributedEnable(bindInterface?: string): Promise<DistributedEnableResponse> {
  return invoke<DistributedEnableResponse>("distributed_enable", {
    args: { bindInterface: bindInterface ?? null },
  });
}

export function distributedDisable(): Promise<DistributedDisableResponse> {
  return invoke<DistributedDisableResponse>("distributed_disable");
}

export function pairingsList(): Promise<PairingRow[]> {
  return invoke<PairingRow[]>("pairings_list");
}

export function pairingsRevoke(id: string): Promise<unknown> {
  return invoke("pairings_revoke", { id });
}

export function listNetworkInterfaces(): Promise<NetworkInterface[]> {
  return invoke<NetworkInterface[]>("list_network_interfaces");
}

/**
 * Format a lowercase-hex SHA-256 fingerprint as colon-separated uppercase byte pairs:
 * `a1b2c3...` → `A1:B2:C3:...`. Used in the UI for readability; the wire/storage form
 * stays lowercase hex as documented in design §3.6.
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
