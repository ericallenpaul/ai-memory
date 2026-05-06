import { useEffect, useState } from 'react'
import { getStats, getSessions, type StatsResponse, type SessionSummary } from '../api/client'
import { useTheme } from '../ThemeContext'
import MetricsCards from '../components/MetricsCards'
import SessionTable from '../components/SessionTable'

export default function Dashboard() {
  const { colors } = useTheme()
  const [stats, setStats] = useState<StatsResponse | null>(null)
  const [recent, setRecent] = useState<SessionSummary[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    Promise.all([
      getStats().then(setStats),
      getSessions({ limit: '10' }).then(setRecent)
    ]).finally(() => setLoading(false))
  }, [])

  if (loading) return <div style={{ color: colors.textMuted }}>Loading dashboard...</div>

  const fmt = (n: number) => n.toLocaleString()
  const fmtCost = (n: number) => `$${n.toFixed(2)}`

  const headingStyle: React.CSSProperties = { fontSize: '1.5rem', fontWeight: 700, marginBottom: '1.5rem' }
  const subheadingStyle: React.CSSProperties = { fontSize: '1rem', fontWeight: 600, marginBottom: '0.75rem', color: colors.textSecondary }
  const chartContainerStyle: React.CSSProperties = { background: colors.bgCard, borderRadius: 8, padding: '1rem', border: `1px solid ${colors.borderDefault}`, marginBottom: '2rem' }

  return (
    <div>
      <h1 style={headingStyle}>Dashboard</h1>
      {stats && (
        <MetricsCards cards={[
          { label: 'Sessions', value: fmt(stats.totalSessions), sub: `${stats.sessionsToday} today` },
          { label: 'Messages', value: fmt(stats.totalMessages) },
          { label: 'Tokens In', value: fmt(stats.totalTokensIn) },
          { label: 'Tokens Out', value: fmt(stats.totalTokensOut) },
          { label: 'Total Cost', value: fmtCost(stats.totalCostUsd) },
        ]} />
      )}

      {stats && stats.dailyStats.length > 0 && (
        <div style={chartContainerStyle}>
          <h2 style={subheadingStyle}>Last 30 Days</h2>
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: 120 }}>
            {stats.dailyStats.map(d => {
              const maxMsg = Math.max(...stats.dailyStats.map(x => x.messages), 1)
              const h = Math.max((d.messages / maxMsg) * 100, 2)
              return (
                <div key={d.date} title={`${d.date}: ${d.sessions}s / ${d.messages}m`}
                  style={{ flex: 1, height: h, background: colors.accentPrimary, borderRadius: '2px 2px 0 0', minWidth: 4 }} />
              )
            })}
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: '0.25rem', fontSize: '0.65rem', color: colors.textDim }}>
            <span>{stats.dailyStats[0]?.date}</span>
            <span>{stats.dailyStats[stats.dailyStats.length - 1]?.date}</span>
          </div>
        </div>
      )}

      <h2 style={subheadingStyle}>Recent Sessions</h2>
      <SessionTable sessions={recent} />
    </div>
  )
}
