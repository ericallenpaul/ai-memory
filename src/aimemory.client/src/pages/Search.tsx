import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { searchMessages, type SearchResult } from '../api/client'
import { useTheme } from '../ThemeContext'

export default function Search() {
  const { colors } = useTheme()
  const [query, setQuery] = useState('')
  const [project, setProject] = useState('')
  const [source, setSource] = useState('')
  const [results, setResults] = useState<SearchResult[]>([])
  const [loading, setLoading] = useState(false)
  const [searched, setSearched] = useState(false)
  const navigate = useNavigate()

  async function handleSearch(e: React.FormEvent) {
    e.preventDefault()
    if (!query.trim()) return
    setLoading(true)
    setSearched(true)
    try {
      const params: Record<string, string> = { q: query }
      if (project) params.project = project
      if (source) params.source = source
      const data = await searchMessages(params)
      setResults(data)
    } finally {
      setLoading(false)
    }
  }

  const searchInput: React.CSSProperties = { flex: 1, minWidth: 200, padding: '0.5rem 0.75rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textPrimary, fontSize: '0.875rem' }
  const filterInput: React.CSSProperties = { padding: '0.5rem 0.75rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textPrimary, fontSize: '0.875rem', width: 120 }
  const searchBtn: React.CSSProperties = { padding: '0.5rem 1.25rem', background: colors.accentPrimary, color: '#fff', border: 'none', borderRadius: 6, cursor: 'pointer', fontSize: '0.875rem' }
  const resultCard: React.CSSProperties = { background: colors.bgCard, borderRadius: 8, padding: '0.75rem 1rem', border: `1px solid ${colors.borderDefault}`, cursor: 'pointer' }
  const roleBadge: React.CSSProperties = { display: 'inline-block', background: colors.bgElevated, borderRadius: 4, padding: '0.05rem 0.4rem', fontSize: '0.7rem', color: colors.textMuted }

  return (
    <div>
      <h1 style={{ fontSize: '1.5rem', fontWeight: 700, marginBottom: '1.5rem' }}>Search</h1>

      <form onSubmit={handleSearch} style={{ display: 'flex', gap: '0.5rem', marginBottom: '1.5rem', flexWrap: 'wrap' }}>
        <input style={searchInput} value={query} onChange={e => setQuery(e.target.value)}
          placeholder="Search messages..." autoFocus />
        <input style={{ ...filterInput }} placeholder="Project" value={project} onChange={e => setProject(e.target.value)} />
        <input style={{ ...filterInput }} placeholder="Source" value={source} onChange={e => setSource(e.target.value)} />
        <button type="submit" style={searchBtn} disabled={loading}>{loading ? 'Searching...' : 'Search'}</button>
      </form>

      {searched && results.length === 0 && !loading && (
        <div style={{ color: colors.textDim, textAlign: 'center', padding: '2rem' }}>No results found</div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
        {results.map((r, i) => (
          <div key={i} onClick={() => navigate(`/sessions/${r.sessionId}`)} style={resultCard}>
            <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '0.25rem' }}>
              <span style={{ fontWeight: 600, color: colors.textPrimary, fontSize: '0.875rem' }}>{r.sessionTitle ?? 'Untitled'}</span>
              <span style={{ fontSize: '0.7rem', color: colors.textDim }}>
                {r.project && `${r.project} | `}
                {new Date(r.createdAt).toLocaleDateString()}
              </span>
            </div>
            <div style={{ fontSize: '0.8rem', color: colors.textMuted }}>
              <span style={roleBadge}>{r.role}</span>
              {' '}{r.snippet}
            </div>
            <div style={{ fontSize: '0.7rem', color: colors.textDim, marginTop: '0.25rem' }}>
              Relevance: {r.rank.toFixed(3)}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
