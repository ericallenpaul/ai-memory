import { createContext, useContext, ReactNode } from "react";

/**
 * Web-mode replacement for the Tauri shell's runtime.json-driven ApiContext.
 *
 * In the Tauri era this loaded baseUrl + apiKey from %ProgramData%\AIMemory\Api\runtime.json
 * and built X-AIMemory-Api-Key-headed requests. With the SPA now served by Kestrel itself,
 * the API is same-origin and cookie auth (the existing AIMemory.Auth cookie set by
 * /api/auth/login) carries identity — no runtime config to load.
 *
 * The shape stays compatible with the Tauri-era pages: callers do
 * `const { api, status } = useApi(); await api("/api/foo")`. Status is always "ready" now
 * (no async config load), so the loading/error forks in those pages become dead branches
 * that we leave alone for minimal port churn.
 */

type ApiState = "ready";

interface ApiContextValue {
  status: ApiState;
  error?: string;
  refetch: () => Promise<void>;
  /**
   * Cookie-authenticated fetch helper. Returns the raw Response so callers can branch on
   * status/streaming/etc.; callers needing parsed JSON should `.json()` themselves.
   */
  api: (path: string, init?: RequestInit) => Promise<Response>;
}

async function cookieFetch(path: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers);
  if (init?.body && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  return fetch(path, { credentials: "include", ...init, headers });
}

const ApiContext = createContext<ApiContextValue | null>(null);

export function ApiProvider({ children }: { children: ReactNode }) {
  const value: ApiContextValue = {
    status: "ready",
    refetch: async () => { /* no-op — web mode has nothing to refetch */ },
    api: cookieFetch,
  };
  return <ApiContext.Provider value={value}>{children}</ApiContext.Provider>;
}

export function useApi() {
  const ctx = useContext(ApiContext);
  if (!ctx) throw new Error("useApi must be used inside <ApiProvider>");
  return ctx;
}
