import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useApi } from "../api/ApiContext";

interface CodeFile {
  fileId: string;
  filePath: string;
  language: string;
  fileSize: number;
}

interface CodeSymbol {
  symbolId: string;
  symbolKey: string;
  name: string;
  qualifiedName: string;
  kind: string;
  signature: string | null;
  startLine: number;
  endLine: number;
}

/**
 * Three-pane layout: file tree on the left, symbol outline of the selected file in
 * the middle, source preview of the selected symbol on the right.
 */
export default function RepoDetail() {
  const { repoId = "" } = useParams<{ repoId: string }>();
  const { api } = useApi();

  const [files, setFiles] = useState<CodeFile[]>([]);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [symbols, setSymbols] = useState<CodeSymbol[]>([]);
  const [selectedSymbol, setSelectedSymbol] = useState<string | null>(null);
  const [symbolSource, setSymbolSource] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!repoId) return;
    (async () => {
      try {
        const res = await api(`/api/code/repos/${repoId}/tree`);
        if (!res.ok) throw new Error(`tree: ${res.status}`);
        const data = await res.json();
        setFiles(data.files ?? data ?? []);
      } catch (e) { setError(String(e)); }
    })();
  }, [repoId]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!selectedFile) { setSymbols([]); return; }
    (async () => {
      try {
        const res = await api(`/api/code/repos/${repoId}/outline?file=${encodeURIComponent(selectedFile)}`);
        if (!res.ok) throw new Error(`outline: ${res.status}`);
        const data = await res.json();
        setSymbols(data.symbols ?? data ?? []);
      } catch (e) { setError(String(e)); }
    })();
  }, [selectedFile]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!selectedSymbol) { setSymbolSource(null); return; }
    (async () => {
      try {
        const res = await api(`/api/code/repos/${repoId}/symbol?key=${encodeURIComponent(selectedSymbol)}`);
        if (!res.ok) throw new Error(`symbol: ${res.status}`);
        const data = await res.json();
        setSymbolSource(data.sourceCode ?? data.source ?? JSON.stringify(data, null, 2));
      } catch (e) { setError(String(e)); }
    })();
  }, [selectedSymbol]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div>
      <div className="toolbar">
        <Link to="/repos" className="btn secondary">← Back</Link>
        <h1 style={{ marginLeft: 12, marginBottom: 0, flex: 1 }}>Repository {repoId.slice(0, 8)}</h1>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr 2fr", gap: 12, height: "calc(100vh - 200px)" }}>
        <div className="card" style={{ overflowY: "auto" }}>
          <h2>Files ({files.length})</h2>
          {files.map((f) => (
            <div
              key={f.fileId}
              className="mono"
              style={{
                padding: "4px 8px",
                cursor: "pointer",
                background: selectedFile === f.filePath ? "rgba(59,130,246,0.15)" : "transparent",
                borderRadius: 4,
              }}
              onClick={() => { setSelectedFile(f.filePath); setSelectedSymbol(null); }}
            >
              {f.filePath}
            </div>
          ))}
        </div>

        <div className="card" style={{ overflowY: "auto" }}>
          <h2>Symbols ({symbols.length})</h2>
          {!selectedFile && <div className="empty-state">Select a file</div>}
          {symbols.map((s) => (
            <div
              key={s.symbolId}
              style={{
                padding: "4px 8px",
                cursor: "pointer",
                background: selectedSymbol === s.symbolKey ? "rgba(59,130,246,0.15)" : "transparent",
                borderRadius: 4,
              }}
              onClick={() => setSelectedSymbol(s.symbolKey)}
            >
              <span style={{ color: "var(--text-dim)", fontSize: 12 }}>{s.kind}</span>{" "}
              <span className="mono">{s.name}</span>
            </div>
          ))}
        </div>

        <div className="card" style={{ overflowY: "auto" }}>
          <h2>Source</h2>
          {!selectedSymbol && <div className="empty-state">Select a symbol</div>}
          {symbolSource && <pre style={{ whiteSpace: "pre-wrap" }}>{symbolSource}</pre>}
        </div>
      </div>
    </div>
  );
}
