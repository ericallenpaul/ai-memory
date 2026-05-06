import { useEffect, useState } from 'react'
import { getApiKeys, createApiKey, updateApiKey, deleteApiKey, type ApiKeyResponse, type CreateApiKeyResponse } from '../api/client'
import { useTheme } from '../ThemeContext'

const SCOPE_LABELS: Record<string, string> = {
  ingest: 'Log Ingestor',
  code: 'Code Index',
  mcp: 'MCP Server',
  admin: 'Full Access',
}

export default function ApiKeys() {
  const { colors } = useTheme()
  const [keys, setKeys] = useState<ApiKeyResponse[]>([])
  const [loading, setLoading] = useState(true)
  const [showCreate, setShowCreate] = useState(false)
  const [newKeyResult, setNewKeyResult] = useState<CreateApiKeyResponse | null>(null)
  const [createName, setCreateName] = useState('')
  const [createScopes, setCreateScopes] = useState<string[]>([])
  const [creating, setCreating] = useState(false)

  useEffect(() => { loadKeys() }, [])

  async function loadKeys() {
    setLoading(true)
    try { setKeys(await getApiKeys()) } finally { setLoading(false) }
  }

  async function handleCreate() {
    if (!createName.trim() || createScopes.length === 0) return
    setCreating(true)
    try {
      const result = await createApiKey({ name: createName.trim(), scopes: createScopes })
      setNewKeyResult(result)
      setShowCreate(false)
      setCreateName('')
      setCreateScopes([])
      await loadKeys()
    } finally { setCreating(false) }
  }

  async function handleRevoke(id: string) {
    await updateApiKey(id, { isActive: false })
    await loadKeys()
  }

  async function handleDelete(id: string) {
    if (!confirm('Permanently delete this API key?')) return
    await deleteApiKey(id)
    await loadKeys()
  }

  function toggleScope(scope: string) {
    setCreateScopes(prev => prev.includes(scope) ? prev.filter(s => s !== scope) : [...prev, scope])
  }

  const thStyle: React.CSSProperties = { textAlign: 'left', padding: '0.5rem 0.75rem', fontSize: '0.75rem', color: colors.textDim, borderBottom: `1px solid ${colors.borderSubtle}`, textTransform: 'uppercase', letterSpacing: 1 }
  const tdStyle: React.CSSProperties = { padding: '0.5rem 0.75rem', fontSize: '0.8rem', color: colors.textBody, borderBottom: `1px solid ${colors.borderSubtle}`, whiteSpace: 'nowrap' }
  const btnStyle: React.CSSProperties = { padding: '0.375rem 1rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textSecondary, fontSize: '0.8rem', cursor: 'pointer' }
  const primaryBtnStyle: React.CSSProperties = { ...btnStyle, background: colors.accentPrimary, color: '#fff', border: 'none' }
  const inputStyle: React.CSSProperties = { padding: '0.375rem 0.75rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textBody, fontSize: '0.85rem', width: '100%' }
  const badgeStyle = (active: boolean): React.CSSProperties => ({
    display: 'inline-block', padding: '0.15rem 0.5rem', borderRadius: 12,
    fontSize: '0.7rem', fontWeight: 600,
    background: active ? colors.accentPrimary + '22' : colors.borderSubtle,
    color: active ? colors.accentPrimary : colors.textDim,
    marginRight: '0.25rem'
  })

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.5rem' }}>
        <h1 style={{ fontSize: '1.5rem', fontWeight: 700 }}>API Keys</h1>
        <button style={primaryBtnStyle} onClick={() => { setShowCreate(true); setNewKeyResult(null) }}>Create Key</button>
      </div>

      {newKeyResult && (
        <div style={{ background: colors.statusGreen + '15', border: `1px solid ${colors.statusGreen}40`, borderRadius: 8, padding: '1rem', marginBottom: '1rem' }}>
          <div style={{ fontWeight: 600, marginBottom: '0.5rem', color: colors.textPrimary }}>Key created: {newKeyResult.name}</div>
          <div style={{ fontFamily: 'monospace', fontSize: '0.85rem', padding: '0.5rem', background: colors.bgDeep, borderRadius: 4, wordBreak: 'break-all', color: colors.textBody }}>{newKeyResult.rawKey}</div>
          <div style={{ fontSize: '0.75rem', color: colors.statusAmber, marginTop: '0.5rem' }}>Save this key now — it won't be shown again.</div>
          <button style={{ ...btnStyle, marginTop: '0.5rem' }} onClick={() => { navigator.clipboard.writeText(newKeyResult.rawKey); }}>Copy to clipboard</button>
        </div>
      )}

      {showCreate && (
        <div style={{ background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 8, padding: '1rem', marginBottom: '1rem' }}>
          <div style={{ marginBottom: '0.75rem' }}>
            <label style={{ fontSize: '0.8rem', color: colors.textMuted, display: 'block', marginBottom: '0.25rem' }}>Name</label>
            <input style={inputStyle} value={createName} onChange={e => setCreateName(e.target.value)} placeholder="e.g. My Ingestor" />
          </div>
          <div style={{ marginBottom: '0.75rem' }}>
            <label style={{ fontSize: '0.8rem', color: colors.textMuted, display: 'block', marginBottom: '0.5rem' }}>Scopes</label>
            <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
              {Object.entries(SCOPE_LABELS).map(([scope, label]) => (
                <label key={scope} style={{ display: 'flex', alignItems: 'center', gap: '0.35rem', fontSize: '0.85rem', color: colors.textBody, cursor: 'pointer' }}>
                  <input type="checkbox" checked={createScopes.includes(scope)} onChange={() => toggleScope(scope)} />
                  {label}
                </label>
              ))}
            </div>
          </div>
          <div style={{ display: 'flex', gap: '0.5rem' }}>
            <button style={primaryBtnStyle} onClick={handleCreate} disabled={creating || !createName.trim() || createScopes.length === 0}>
              {creating ? 'Creating...' : 'Create'}
            </button>
            <button style={btnStyle} onClick={() => setShowCreate(false)}>Cancel</button>
          </div>
        </div>
      )}

      {loading ? (
        <div style={{ color: colors.textMuted }}>Loading...</div>
      ) : keys.length === 0 ? (
        <div style={{ color: colors.textDim, textAlign: 'center', padding: '2rem' }}>No API keys yet</div>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              {['Name', 'Key', 'Scopes', 'Status', 'Last Used', 'Actions'].map(h => (
                <th key={h} style={thStyle}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {keys.map(k => (
              <tr key={k.apiKeyId}>
                <td style={tdStyle}>{k.name}</td>
                <td style={{ ...tdStyle, fontFamily: 'monospace' }}>aimemory_{k.keyPrefix}...</td>
                <td style={tdStyle}>
                  {k.scopes.map(s => <span key={s} style={badgeStyle(k.isActive)}>{SCOPE_LABELS[s] ?? s}</span>)}
                </td>
                <td style={tdStyle}>
                  <span style={{ color: k.isActive ? colors.statusGreen : colors.statusRed }}>
                    {k.isActive ? 'Active' : 'Revoked'}
                  </span>
                </td>
                <td style={tdStyle}>{k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : 'Never'}</td>
                <td style={tdStyle}>
                  <div style={{ display: 'flex', gap: '0.35rem' }}>
                    {k.isActive && (
                      <button style={{ ...btnStyle, fontSize: '0.75rem', padding: '0.25rem 0.5rem' }} onClick={() => handleRevoke(k.apiKeyId)}>Revoke</button>
                    )}
                    <button style={{ ...btnStyle, fontSize: '0.75rem', padding: '0.25rem 0.5rem', color: colors.statusRed }} onClick={() => handleDelete(k.apiKeyId)}>Delete</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
