import { useEffect, useState } from "react";
import { useApi } from "../api/ApiContext";

const TABLES = [
  "code_repositories",
  "code_files",
  "code_symbols",
  "ingestion_log",
] as const;

interface TableData {
  columns: string[];
  rows: Record<string, unknown>[];
  total: number;
}

/**
 * Read-only inspector over the SQLite tables relevant to the code indexer. Calls
 * into /api/admin/tables/{name} which is added in Phase 3.6 — until then this page
 * shows an "endpoint not yet available" notice for graceful degradation.
 */
export function DbBrowser() {
  const { status, api } = useApi();
  const [table, setTable] = useState<typeof TABLES[number]>("code_repositories");
  const [data, setData] = useState<TableData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const load = async () => {
    if (status !== "ready") return;
    setLoading(true);
    setError(null);
    try {
      const res = await api(`/api/admin/tables/${table}?limit=50&offset=0`);
      if (res.status === 404) {
        setError("Admin endpoint not yet implemented (Phase 3.6).");
        setData(null);
        return;
      }
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      setData(await res.json());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [table, status]);

  return (
    <div>
      <h1>Database Browser</h1>
      <div className="toolbar">
        <select value={table} onChange={(e) => setTable(e.target.value as typeof TABLES[number])}>
          {TABLES.map((t) => <option key={t}>{t}</option>)}
        </select>
        <button className="btn secondary" onClick={load} disabled={loading}>Refresh</button>
        {data && <span style={{ color: "var(--text-dim)" }}>{data.total} rows total</span>}
      </div>

      {error && <div className="error-banner">{error}</div>}

      {data && (
        <div className="card" style={{ padding: 0, overflowX: "auto" }}>
          <table>
            <thead>
              <tr>{data.columns.map((c) => <th key={c}>{c}</th>)}</tr>
            </thead>
            <tbody>
              {data.rows.map((row, i) => (
                <tr key={i}>
                  {data.columns.map((c) => (
                    <td key={c} className="mono" style={{ maxWidth: 300, overflow: "hidden", textOverflow: "ellipsis" }}>
                      {formatCell(row[c])}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function formatCell(v: unknown): string {
  if (v === null || v === undefined) return "";
  if (typeof v === "object") return JSON.stringify(v);
  return String(v);
}
