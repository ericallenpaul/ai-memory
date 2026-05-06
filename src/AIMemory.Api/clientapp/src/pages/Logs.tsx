import { useEffect, useState } from 'react'
import { getIngestionLog, type IngestionLogEntry } from '../api/client'
import { useTheme } from '../ThemeContext'

export default function Logs() {
  const { colors } = useTheme()
  const [entries, setEntries] = useState<IngestionLogEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [source, setSource] = useState('')
  const [eventType, setEventType] = useState('')
  const [page, setPage] = useState(0)
  const pageSize = 50

  useEffect(() => { loadLogs() }, [source, eventType, page])

  async function loadLogs() {
    setLoading(true)
    const params: Record<string, string> = { limit: String(pageSize), offset: String(page * pageSize) }
    if (source) params.source = source
    if (eventType) params.eventType = eventType
    try {
      const data = await getIngestionLog(params)
      setEntries(data)
    } finally {
      setLoading(false)
    }
  }

  const sources = [...new Set(entries.map(e => e.source).filter(Boolean))]
  const eventTypes = [...new Set(entries.map(e => e.eventType).filter(Boolean))]

  const statusColor = (s: string) => s === 'ok' ? colors.statusGreen : s === 'duplicate' ? colors.statusAmber : colors.statusRed

  const selectStyle: React.CSSProperties = { padding: '0.375rem 0.75rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textSecondary, fontSize: '0.8rem' }
  const thStyle: React.CSSProperties = { textAlign: 'left', padding: '0.5rem 0.75rem', fontSize: '0.75rem', color: colors.textDim, borderBottom: `1px solid ${colors.borderSubtle}`, textTransform: 'uppercase', letterSpacing: 1 }
  const tdStyle: React.CSSProperties = { padding: '0.5rem 0.75rem', fontSize: '0.8rem', color: colors.textBody, borderBottom: `1px solid ${colors.borderSubtle}`, whiteSpace: 'nowrap' }
  const pageBtnStyle: React.CSSProperties = { padding: '0.375rem 1rem', background: colors.bgCard, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textSecondary, fontSize: '0.8rem', cursor: 'pointer' }

  return (
    <div>
      <h1 style={{ fontSize: '1.5rem', fontWeight: 700, marginBottom: '1.5rem' }}>Ingestion Logs</h1>

      <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '1rem', flexWrap: 'wrap' }}>
        <select style={selectStyle} value={source} onChange={e => { setSource(e.target.value); setPage(0) }}>
          <option value="">All Sources</option>
          {sources.map(s => <option key={s} value={s}>{s}</option>)}
        </select>
        <select style={selectStyle} value={eventType} onChange={e => { setEventType(e.target.value); setPage(0) }}>
          <option value="">All Events</option>
          {eventTypes.map(t => <option key={t} value={t}>{t}</option>)}
        </select>
      </div>

      {loading ? (
        <div style={{ color: colors.textMuted }}>Loading...</div>
      ) : entries.length === 0 ? (
        <div style={{ color: colors.textDim, textAlign: 'center', padding: '2rem' }}>No log entries found</div>
      ) : (
        <>
          <table style={{ width: '100%', borderCollapse: 'collapse' }}>
            <thead>
              <tr>
                {['Timestamp', 'Event', 'Source', 'Path', 'Status'].map(h => (
                  <th key={h} style={thStyle}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {entries.map(e => (
                <tr key={e.idempotencyKey}>
                  <td style={tdStyle}>{new Date(e.createdAt).toLocaleString()}</td>
                  <td style={tdStyle}>{e.eventType}</td>
                  <td style={tdStyle}>{e.source}</td>
                  <td style={{ ...tdStyle, maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis' }}>{e.sourcePath}</td>
                  <td style={tdStyle}><span style={{ color: statusColor(e.status) }}>{e.status}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ display: 'flex', gap: '0.5rem', marginTop: '1rem', justifyContent: 'center' }}>
            <button style={pageBtnStyle} disabled={page === 0} onClick={() => setPage(p => p - 1)}>Previous</button>
            <span style={{ color: colors.textDim, fontSize: '0.85rem', padding: '0.5rem' }}>Page {page + 1}</span>
            <button style={pageBtnStyle} disabled={entries.length < pageSize} onClick={() => setPage(p => p + 1)}>Next</button>
          </div>
        </>
      )}
    </div>
  )
}
