import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useApi } from "../api/ApiContext";
import { validatePath } from "../api/admin";

interface CodeRepoSummary {
  repositoryId: string;
  name: string;
  sourcePath: string;
  fileCount: number;
  symbolCount: number;
  indexedAt: string;
  updatedAt: string;
}

export default function Repos() {
  const { api } = useApi();
  const [repos, setRepos] = useState<CodeRepoSummary[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  const [pathInput, setPathInput] = useState("");
  const [validateState, setValidateState] = useState<"idle" | "validating" | "ok" | "bad">("idle");
  const [validateMessage, setValidateMessage] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const fetchRepos = async () => {
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

  useEffect(() => { fetchRepos(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const handleValidate = async () => {
    if (!pathInput.trim()) return;
    setValidateState("validating");
    setValidateMessage(null);
    try {
      const result = await validatePath(api, pathInput.trim());
      if (!result.exists) {
        setValidateState("bad");
        setValidateMessage(`Path does not exist: ${result.path}`);
      } else if (!result.isDirectory) {
        setValidateState("bad");
        setValidateMessage(`Path exists but is not a directory: ${result.path}`);
      } else if (!result.isGitRepo) {
        setValidateState("bad");
        setValidateMessage(`Directory is not a git repository (no .git): ${result.path}`);
      } else {
        setValidateState("ok");
        setValidateMessage(`Valid git repo: ${result.path}`);
        setPathInput(result.path); // canonicalize
      }
    } catch (e) {
      setValidateState("bad");
      setValidateMessage(e instanceof Error ? e.message : String(e));
    }
  };

  const handleAdd = async () => {
    if (validateState !== "ok") return;
    setAdding(true);
    setPageError(null);
    try {
      const res = await api("/api/code/index-folder", {
        method: "POST",
        body: JSON.stringify({ folderPath: pathInput.trim() }),
      });
      if (!res.ok) throw new Error(`API returned ${res.status}`);
      await fetchRepos();
      setShowAdd(false);
      setPathInput("");
      setValidateState("idle");
      setValidateMessage(null);
    } catch (e) {
      setPageError(e instanceof Error ? e.message : String(e));
    } finally {
      setAdding(false);
    }
  };

  const handleReindex = async (repoId: string) => {
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

  return (
    <div>
      <div className="toolbar">
        <h1 style={{ flex: 1, marginBottom: 0 }}>Repositories</h1>
        <button className="btn" onClick={() => setShowAdd((v) => !v)} disabled={loading}>
          {showAdd ? "Cancel" : "Add folder"}
        </button>
        <button className="btn secondary" onClick={fetchRepos} disabled={loading}>
          Refresh
        </button>
      </div>

      {showAdd && (
        <div className="card">
          <h2 style={{ marginTop: 0 }}>Add repository</h2>
          <p style={{ color: "var(--text-dim)", fontSize: 13 }}>
            Browsers can't open a native folder picker, so paste the absolute path to a local git
            repository. Validation runs on the server and checks the path exists, is a directory,
            and contains a <code className="mono">.git</code> entry.
          </p>
          <div className="toolbar">
            <input
              className="admin-input"
              style={{ flex: 1 }}
              type="text"
              placeholder="C:\Users\you\src\my-repo"
              value={pathInput}
              onChange={(e) => { setPathInput(e.target.value); setValidateState("idle"); setValidateMessage(null); }}
            />
            <button className="btn secondary" onClick={handleValidate}
              disabled={!pathInput.trim() || validateState === "validating"}>
              {validateState === "validating" ? "Checking…" : "Validate"}
            </button>
            <button className="btn" onClick={handleAdd}
              disabled={validateState !== "ok" || adding}>
              {adding ? "Adding…" : "Add"}
            </button>
          </div>
          {validateMessage && (
            <div className={validateState === "ok" ? "" : "error-banner"}
                 style={validateState === "ok"
                   ? { color: "var(--success)", marginTop: 8, fontSize: 13 }
                   : { marginTop: 8 }}>
              {validateMessage}
            </div>
          )}
        </div>
      )}

      {pageError && <div className="error-banner">{pageError}</div>}

      {repos === null ? (
        <div className="empty-state">Loading repositories…</div>
      ) : repos.length === 0 ? (
        <div className="empty-state">
          No repositories indexed yet. Click <strong>Add folder</strong> to start.
        </div>
      ) : (
        <div className="card" style={{ padding: 0 }}>
          <table>
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
        </div>
      )}
    </div>
  );
}
