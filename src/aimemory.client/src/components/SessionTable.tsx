import { useNavigate } from 'react-router-dom'
import type { SessionSummary } from '../api/client'
import { useTheme } from '../ThemeContext'

export default function SessionTable({ sessions }: { sessions: SessionSummary[] }) {
  const { colors } = useTheme()
  const navigate = useNavigate()

  if (sessions.length === 0)
    return <div style={{ color: colors.textDim, padding: '2rem', textAlign: 'center' }}>No sessions found</div>

  const thStyle: React.CSSProperties = {
    textAlign: 'left', padding: '0.5rem 0.75rem', fontSize: '0.75rem',
    color: colors.textDim, borderBottom: `1px solid ${colors.borderSubtle}`, textTransform: 'uppercase', letterSpacing: 1
  }
  const tdStyle: React.CSSProperties = {
    padding: '0.625rem 0.75rem', fontSize: '0.875rem', color: colors.textBody,
    borderBottom: `1px solid ${colors.borderSubtle}`
  }
  const tagStyle: React.CSSProperties = {
    display: 'inline-block', background: colors.bgElevated, color: colors.textMuted, borderRadius: 4,
    padding: '0.125rem 0.5rem', fontSize: '0.7rem', marginRight: 4
  }

  return (
    <table style={{ width: '100%', borderCollapse: 'collapse' }}>
      <thead>
        <tr>
          {['Title', 'Project', 'Source', 'Tags', 'Messages', 'Created'].map(h => (
            <th key={h} style={thStyle}>{h}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {sessions.map(s => (
          <tr key={s.sessionId} onClick={() => navigate(`/sessions/${s.sessionId}`)}
            style={{ cursor: 'pointer' }}
            onMouseEnter={e => (e.currentTarget.style.background = colors.bgCard)}
            onMouseLeave={e => (e.currentTarget.style.background = 'transparent')}>
            <td style={tdStyle}>
              <div style={{ fontWeight: 500, color: colors.textPrimary }}>{s.title || 'Untitled'}</div>
            </td>
            <td style={tdStyle}>{s.project ?? '-'}</td>
            <td style={tdStyle}>{s.source ?? '-'}</td>
            <td style={tdStyle}>
              {s.tags.map(t => (
                <span key={t} style={tagStyle}>{t}</span>
              ))}
            </td>
            <td style={tdStyle}>{s.messageCount}</td>
            <td style={tdStyle}>{new Date(s.createdAt).toLocaleDateString()}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
