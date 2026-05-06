import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { getSession, type SessionDetail as SessionDetailType } from '../api/client'
import { useTheme } from '../ThemeContext'

export default function SessionDetail() {
  const { colors } = useTheme()
  const { id } = useParams<{ id: string }>()
  const [session, setSession] = useState<SessionDetailType | null>(null)
  const [loading, setLoading] = useState(true)
  const [expandedToolCalls, setExpandedToolCalls] = useState<Set<string>>(new Set())

  useEffect(() => {
    if (id) getSession(id).then(setSession).finally(() => setLoading(false))
  }, [id])

  function toggleToolCall(tcId: string) {
    setExpandedToolCalls(prev => {
      const next = new Set(prev)
      next.has(tcId) ? next.delete(tcId) : next.add(tcId)
      return next
    })
  }

  if (loading) return <div style={{ color: colors.textMuted }}>Loading session...</div>
  if (!session) return <div style={{ color: colors.errorText }}>Session not found</div>

  const roleColors: Record<string, string> = {
    user: colors.accentPrimary, assistant: colors.statusGreen, system: colors.statusPurple, tool: colors.statusAmber
  }

  const sectionTitle: React.CSSProperties = { fontSize: '1rem', fontWeight: 600, marginBottom: '0.75rem', color: colors.textSecondary }
  const tagStyle: React.CSSProperties = { display: 'inline-block', background: colors.bgElevated, color: colors.textMuted, borderRadius: 4, padding: '0.125rem 0.5rem', fontSize: '0.7rem', marginRight: 4 }
  const msgCard: React.CSSProperties = { background: colors.bgCard, borderRadius: 8, padding: '0.75rem 1rem', borderLeft: '3px solid', border: `1px solid ${colors.borderDefault}` }
  const preStyle: React.CSSProperties = { whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontSize: '0.8rem', color: colors.textBody, margin: 0, lineHeight: 1.5 }
  const toolCallCard: React.CSSProperties = { background: colors.bgCard, borderRadius: 8, padding: '0.75rem 1rem', border: `1px solid ${colors.borderDefault}` }
  const jsonLabel: React.CSSProperties = { fontSize: '0.7rem', color: colors.textDim, textTransform: 'uppercase', marginBottom: '0.25rem' }
  const jsonPre: React.CSSProperties = { whiteSpace: 'pre-wrap', fontSize: '0.75rem', color: colors.textMuted, background: colors.bgDeep, padding: '0.5rem', borderRadius: 4, margin: '0 0 0.5rem 0', maxHeight: 300, overflow: 'auto' }
  const artifactCard: React.CSSProperties = { background: colors.bgCard, borderRadius: 8, padding: '0.75rem 1rem', border: `1px solid ${colors.borderDefault}` }

  return (
    <div>
      <Link to="/sessions" style={{ color: colors.textMuted, textDecoration: 'none', fontSize: '0.8rem' }}>Back to sessions</Link>
      <h1 style={{ fontSize: '1.5rem', fontWeight: 700, marginTop: '0.5rem', marginBottom: '0.5rem' }}>{session.title}</h1>

      <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap', marginBottom: '1.5rem', fontSize: '0.8rem', color: colors.textMuted }}>
        {session.project && <span>Project: <strong style={{ color: colors.textSecondary }}>{session.project}</strong></span>}
        {session.repo && <span>Repo: <strong style={{ color: colors.textSecondary }}>{session.repo}</strong></span>}
        {session.branch && <span>Branch: <strong style={{ color: colors.textSecondary }}>{session.branch}</strong></span>}
        {session.source && <span>Source: <strong style={{ color: colors.textSecondary }}>{session.source}</strong></span>}
        <span>Created: <strong style={{ color: colors.textSecondary }}>{new Date(session.createdAt).toLocaleString()}</strong></span>
      </div>

      {session.tags.length > 0 && (
        <div style={{ marginBottom: '1.5rem' }}>
          {session.tags.map(t => (
            <span key={t} style={tagStyle}>{t}</span>
          ))}
        </div>
      )}

      {/* Messages */}
      <h2 style={sectionTitle}>Messages ({session.messages?.length ?? 0})</h2>
      <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', marginBottom: '2rem' }}>
        {session.messages?.map(m => (
          <div key={m.messageId} style={{ ...msgCard, borderLeftColor: roleColors[m.role] ?? colors.textDim }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '0.25rem' }}>
              <span style={{ fontWeight: 600, fontSize: '0.8rem', color: roleColors[m.role] ?? colors.textMuted }}>{m.role}</span>
              <span style={{ fontSize: '0.7rem', color: colors.textDim }}>
                {m.model && `${m.model} | `}
                {m.tokenIn != null && `${m.tokenIn}in `}{m.tokenOut != null && `${m.tokenOut}out `}
                {m.costUsd != null && `$${m.costUsd.toFixed(4)}`}
              </span>
            </div>
            <pre style={preStyle}>{m.content}</pre>
          </div>
        ))}
      </div>

      {/* Tool Calls */}
      {session.toolCalls && session.toolCalls.length > 0 && (
        <>
          <h2 style={sectionTitle}>Tool Calls ({session.toolCalls.length})</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', marginBottom: '2rem' }}>
            {session.toolCalls.map(tc => (
              <div key={tc.toolCallId} style={toolCallCard}>
                <div onClick={() => toggleToolCall(tc.toolCallId)}
                  style={{ cursor: 'pointer', display: 'flex', justifyContent: 'space-between' }}>
                  <span style={{ fontWeight: 600, color: colors.statusAmber, fontSize: '0.875rem' }}>{tc.toolName}</span>
                  <span style={{ fontSize: '0.7rem', color: colors.textDim }}>{new Date(tc.createdAt).toLocaleTimeString()}</span>
                </div>
                {expandedToolCalls.has(tc.toolCallId) && (
                  <div style={{ marginTop: '0.5rem' }}>
                    {tc.argumentsJson != null && (
                      <>
                        <div style={jsonLabel}>Arguments</div>
                        <pre style={jsonPre}>{JSON.stringify(tc.argumentsJson, null, 2)}</pre>
                      </>
                    )}
                    {tc.resultJson != null && (
                      <>
                        <div style={jsonLabel}>Result</div>
                        <pre style={jsonPre}>{JSON.stringify(tc.resultJson, null, 2)}</pre>
                      </>
                    )}
                  </div>
                )}
              </div>
            ))}
          </div>
        </>
      )}

      {/* Artifacts */}
      {session.artifacts && session.artifacts.length > 0 && (
        <>
          <h2 style={sectionTitle}>Artifacts ({session.artifacts.length})</h2>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
            {session.artifacts.map(a => (
              <div key={a.artifactId} style={artifactCard}>
                <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                  <span style={{ fontWeight: 500, color: colors.textSecondary }}>{a.type}</span>
                  <span style={{ fontSize: '0.7rem', color: colors.textDim }}>{new Date(a.createdAt).toLocaleTimeString()}</span>
                </div>
                {a.pathOrUrl && <div style={{ fontSize: '0.8rem', color: colors.textMuted, marginTop: '0.25rem' }}>{a.pathOrUrl}</div>}
                {a.hash && <div style={{ fontSize: '0.7rem', color: colors.textDim, fontFamily: 'monospace' }}>{a.hash}</div>}
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
