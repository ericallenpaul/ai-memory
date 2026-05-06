import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { open } from "@tauri-apps/plugin-dialog";
import { useApi } from "../api/ApiContext";

interface CodeRepoSummary {
  repositoryId: string;
  name: string;
  sourcePath: string;
  fileCount: number;
  symbolCount: number;
  indexedAt: string;
  updatedAt: string;
}

export function Repos() {
  const { status, error, api } = useApi();
  const [repos, setRepos] = useState<CodeRepoSummary[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);

  const fetchRepos = async () => {
    if (status !== "ready") return;
    setLoading(true);
    setPageError(null);
    try {
      const res = await api("/api/code/repos");
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      setRepos(await res.json());
    } catch (e) {
      setPageError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchRepos(); }, [status]);

  const handleAddFolder = async () => {
    const folder = await open({ directory: true, multiple: false });
    if (!folder || typeof folder !== "string") return;

    setLoading(true);
    try {
      const res = await api("/api/code/index-folder", {
        method: "POST",
        body: JSON.stringify({ folderPath: folder }),
      });
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      await fetchRepos();
    } catch (e) {
      setPageError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  const handleReindex = async (repoId: string) => {
    if (status !== "ready") return;
    setLoading(true);
    try {
      const res = await api(`/api/code/repos/${repoId}/reindex`, { method: "POST" });
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      await fetchRepos();
    } catch (e) {
      setPageError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  const handleRemove = async (repoId: string) => {
    if (status !== "ready") return;
    if (!confirm("Remove this repository from the index?")) return;
    setLoading(true);
    try {
      const res = await api(`/api/code/repos/${repoId}`, { method: "DELETE" });
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      await fetchRepos();
    } catch (e) {
      setPageError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  if (status === "loading") return <div>Loading runtime config...</div>;
  if (status === "error") return (
    <div className="error-banner">
      Could not load runtime config: {error}
      <br />
      Check that the AIMemory API service is running (Services tab).
    </div>
  );

  return (
    <div>
      <div className="toolbar">
        <h1 style={{ flex: 1, marginBottom: 0 }}>Repositories</h1>
        <button className="btn" onClick={handleAddFolder} disabled={loading}>
          Add folder
        </button>
        <button className="btn secondary" onClick={fetchRepos} disabled={loading}>
          Refresh
        </button>
      </div>

      {pageError && <div className="error-banner">{pageError}</div>}

      {repos === null ? (
        <div className="empty-state">Loading repositories…</div>
      ) : repos.length === 0 ? (
        <div className="empty-state">
          No repositories indexed yet. Click <strong>Add folder</strong> to start.
        </div>
      ) : (
        <table className="card" style={{ padding: 0 }}>
          <thead>
            <tr>
              <th>Name</th>
              <th>Path</th>
              <th>Files</th>
              <th>Symbols</th>
              <th>Last indexed</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {repos.map((r) => (
              <tr key={r.repositoryId}>
                <td><Link to={`/repos/${r.repositoryId}`}>{r.name}</Link></td>
                <td className="mono" style={{ color: "var(--text-dim)" }}>{r.sourcePath}</td>
                <td>{r.fileCount}</td>
                <td>{r.symbolCount}</td>
                <td>{new Date(r.updatedAt).toLocaleString()}</td>
                <td>
                  <button className="btn secondary" onClick={() => handleReindex(r.repositoryId)} disabled={loading}>
                    Reindex
                  </button>
                  <button className="btn danger" style={{ marginLeft: 8 }} onClick={() => handleRemove(r.repositoryId)} disabled={loading}>
                    Remove
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
