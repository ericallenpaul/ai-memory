import { createContext, useContext, useEffect, useState, ReactNode } from "react";
import { invoke } from "@tauri-apps/api/core";

/**
 * Runtime config the API service writes to %ProgramData%\AIMemory\Api\runtime.json
 * on startup. The Tauri shell loads it once and exposes it to the React tree so every
 * page can build authenticated requests without re-reading the file.
 */
export interface RuntimeConfig {
  baseUrl: string;
  apiKey: string;
  port: number;
}

type ApiState =
  | { status: "loading" }
  | { status: "ready"; config: RuntimeConfig }
  | { status: "error"; error: string };

interface ApiContextValue extends Pick<ApiState, "status"> {
  config?: RuntimeConfig;
  error?: string;
  refetch: () => Promise<void>;
  /**
   * Convenience fetch that prepends the configured base URL and adds the X-API-Key
   * header. Throws if the runtime config hasn't loaded yet — pages should gate on
   * `status === "ready"` before calling.
   */
  api: (path: string, init?: RequestInit) => Promise<Response>;
}

const ApiContext = createContext<ApiContextValue | null>(null);

export function ApiProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<ApiState>({ status: "loading" });

  const load = async () => {
    setState({ status: "loading" });
    try {
      const config = await invoke<RuntimeConfig>("runtime_config");
      setState({ status: "ready", config });
    } catch (e: unknown) {
      // The Rust side returns DesktopError as a JSON-tagged variant ("notfound"
      // when the file doesn't exist yet — services aren't installed/running).
      const error = typeof e === "string" ? e : JSON.stringify(e);
      setState({ status: "error", error });
    }
  };

  useEffect(() => { load(); }, []);

  const api = async (path: string, init?: RequestInit): Promise<Response> => {
    if (state.status !== "ready") {
      throw new Error("API config not loaded yet");
    }
    const url = `${state.config.baseUrl}${path}`;
    const headers = new Headers(init?.headers);
    headers.set("X-API-Key", state.config.apiKey);
    if (init?.body && !headers.has("Content-Type")) {
      headers.set("Content-Type", "application/json");
    }
    return fetch(url, { ...init, headers });
  };

  return (
    <ApiContext.Provider value={{
      status: state.status,
      config: state.status === "ready" ? state.config : undefined,
      error: state.status === "error" ? state.error : undefined,
      refetch: load,
      api,
    }}>
      {children}
    </ApiContext.Provider>
  );
}

export function useApi() {
  const ctx = useContext(ApiContext);
  if (!ctx) throw new Error("useApi must be used inside <ApiProvider>");
  return ctx;
}
