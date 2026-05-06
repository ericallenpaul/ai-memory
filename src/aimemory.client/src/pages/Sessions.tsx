import { useEffect, useState } from 'react'
import { getSessions, type SessionSummary } from '../api/client'
import { useTheme } from '../ThemeContext'
import SessionTable from '../components/SessionTable'

export default function Sessions() {
  const { colors } = useTheme()
  const [sessions, setSessions] = useState<SessionSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [project, setProject] = useState('')
  const [source, setSource] = useState('')
  const [tag, setTag] = useState('')
  const [page, setPage] = useState(0)
  const pageSize = 25

  useEffect(() => { loadSessions() }, [project, source, tag, page])

  async function loadSessions() {
    setLoading(true)
    const params: Record<string, string> = { limit: String(pageSize), offset: String(page * pageSize) }
    if (project) params.project = project
    if (source) params.source = source
    if (tag) params.tag = tag
    try {
      const data = await getSessions(params)
      setSessions(data)
    } finally {
      setLoading(false)
    }
  }

  // Collect unique projects/sources from loaded sessions for filter dropdowns
  const projects = [...new Set(sessions.map(s => s.project).filter(Boolean))] as string[]
  const sources = [...new Set(sessions.map(s => s.source).filter(Boolean))] as string[]

  const selectStyle: React.CSSProperties = {
    padding: '0.375rem 0.75rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`,
    borderRadius: 6, color: colors.textSecondary, fontSize: '0.8rem'
  }
  const pageBtnStyle: React.CSSProperties = {
    padding: '0.375rem 1rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`,
    borderRadius: 6, color: colors.textSecondary, fontSize: '0.8rem', cursor: 'pointer'
  }

  return (
    <div>
      <h1 style={{ fontSize: '1.5rem', fontWeight: 700, marginBottom: '1.5rem' }}>Sessions</h1>

      <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '1rem', flexWrap: 'wrap' }}>
        <select style={selectStyle} value={project} onChange={e => { setProject(e.target.value); setPage(0) }}>
          <option value="">All Projects</option>
          {projects.map(p => <option key={p} value={p}>{p}</option>)}
        </select>
        <select style={selectStyle} value={source} onChange={e => { setSource(e.target.value); setPage(0) }}>
          <option value="">All Sources</option>
          {sources.map(s => <option key={s} value={s}>{s}</option>)}
        </select>
        <input style={{ ...selectStyle, minWidth: 120 }} placeholder="Filter by tag..." value={tag}
          onChange={e => { setTag(e.target.value); setPage(0) }} />
      </div>

      {loading ? (
        <div style={{ color: colors.textMuted }}>Loading...</div>
      ) : (
        <>
          <SessionTable sessions={sessions} />
          <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem', justifyContent: 'center' }}>
            <button style={pageBtnStyle} disabled={page === 0} onClick={() => setPage(p => p - 1)}>Previous</button>
            <span style={{ color: colors.textDim, fontSize: '0.85rem', padding: '0.5rem' }}>Page {page + 1}</span>
            <button style={pageBtnStyle} disabled={sessions.length < pageSize} onClick={() => setPage(p => p + 1)}>Next</button>
          </div>
        </>
      )}
    </div>
  )
}
