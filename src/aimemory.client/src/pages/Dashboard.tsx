import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getStats, getSessions,
  type StatsResponse, type SessionSummary,
} from '../api/client'
import { useApi } from '../api/ApiContext'
import { distributedStatus, pairingsList, type PairingRow, type DistributedStatus } from '../api/admin'
import MetricCard from '../components/MetricCard'
import EmptyState from '../components/EmptyState'
import { PillTabs } from '../components/Pills'
import { IconChat, IconActivity, IconRepo, IconDistributed } from '../components/Icons'

interface CodeRepoSummary {
  repositoryId: string
  name: string
  sourcePath: string
  fileCount: number
  symbolCount: number
  updatedAt: string
}

type ChartMode = 'usage' | 'cost'

export default function Dashboard() {
  const { api } = useApi()
  const [stats, setStats] = useState<StatsResponse | null>(null)
  const [recent, setRecent] = useState<SessionSummary[] | null>(null)
  const [repos, setRepos] = useState<CodeRepoSummary[] | null>(null)
  const [dist, setDist] = useState<DistributedStatus | null>(null)
  const [pairings, setPairings] = useState<PairingRow[] | null>(null)
  const [chartMode, setChartMode] = useState<ChartMode>('usage')

  useEffect(() => {
    getStats().then(setStats).catch(() => setStats(null))
    getSessions({ limit: '5' }).then(setRecent).catch(() => setRecent([]))
    api('/api/code/repos').then(async (r) => {
      if (r.ok) setRepos(await r.json()); else setRepos([])
    }).catch(() => setRepos([]))
    distributedStatus(api).then(setDist).catch(() => setDist(null))
    pairingsList(api).then(setPairings).catch(() => setPairings([]))
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const fmt = (n: number) => n.toLocaleString()
  const fmtCost = (n: number) => `$${n.toFixed(2)}`

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--gutter)' }}>
      {/* Metric row */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(5, 1fr)', gap: 'var(--gutter)' }}>
        <MetricCard
          label="Sessions"
          value={fmt(stats?.totalSessions ?? 0)}
          subtext={stats ? `${stats.sessionsToday} today` : '—'}
        />
        <MetricCard label="Messages" value={fmt(stats?.totalMessages ?? 0)} />
        <MetricCard label="Tokens In" value={fmt(stats?.totalTokensIn ?? 0)} />
        <MetricCard label="Tokens Out" value={fmt(stats?.totalTokensOut ?? 0)} />
        <MetricCard label="Total Cost" value={fmtCost(stats?.totalCostUsd ?? 0)} />
      </div>

      {/* Last 30 Days chart card */}
      <div className="card" style={{ margin: 0 }}>
        <div className="card-header">
          <h2>Last 30 Days</h2>
          <PillTabs<ChartMode>
            value={chartMode}
            options={[{ value: 'usage', label: 'Usage' }, { value: 'cost', label: 'Cost' }]}
            onChange={setChartMode}
          />
        </div>
        <DailyChart stats={stats} mode={chartMode} />
      </div>

      {/* Recent Sessions */}
      <div className="card" style={{ margin: 0 }}>
        <div className="card-header">
          <h2>Recent Sessions</h2>
          <Link to="/sessions" style={{ fontSize: 13, color: 'var(--primary)' }}>View All</Link>
        </div>
        {recent === null ? (
          <div style={{ padding: '32px 0', textAlign: 'center', color: 'var(--text-muted)' }}>Loading…</div>
        ) : recent.length === 0 ? (
          <EmptyState
            icon={<IconChat size={24} />}
            title="No sessions found"
            description="Recent API interactions and user sessions will appear here once you begin processing LLM requests."
            action={<Link to="/keys" className="btn">Get API Keys</Link>}
          />
        ) : (
          <RecentSessionsList sessions={recent} />
        )}
      </div>

      {/* Bottom row: Active Repositories + Network Distribution */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 'var(--gutter)' }}>
        <ActiveRepositoriesCard repos={repos} />
        <NetworkDistributionCard dist={dist} pairings={pairings} />
      </div>
    </div>
  )
}

/* ---------- Daily chart ---------- */

function DailyChart({ stats, mode }: { stats: StatsResponse | null; mode: ChartMode }) {
  if (!stats || stats.dailyStats.length === 0) {
    return (
      <EmptyState
        icon={<IconActivity size={24} />}
        title="No data to display"
        description="No sufficient data to visualize activity"
      />
    )
  }
  const values = stats.dailyStats.map((d) =>
    mode === 'usage' ? d.messages : 0 // backend's dailyStats has sessions+messages only; cost row is a placeholder
  )
  const max = Math.max(...values, 1)
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'flex-end', gap: 4, height: 160 }}>
        {stats.dailyStats.map((d, i) => {
          const v = values[i]
          const h = Math.max((v / max) * 100, 2)
          return (
            <div
              key={d.date}
              title={`${d.date}: ${d.sessions} sessions / ${d.messages} messages`}
              style={{
                flex: 1,
                height: `${h}%`,
                minWidth: 4,
                background: 'var(--primary)',
                opacity: v === 0 ? 0.18 : 0.85,
                borderRadius: '4px 4px 0 0',
              }}
            />
          )
        })}
      </div>
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        marginTop: 8,
        fontSize: 11,
        color: 'var(--text-muted)',
      }}>
        <span>{stats.dailyStats[0]?.date}</span>
        <span>{stats.dailyStats[stats.dailyStats.length - 1]?.date}</span>
      </div>
      {mode === 'cost' && (
        <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 12, textAlign: 'center' }}>
          Cost breakdown by day isn't surfaced by /api/stats yet — wire that in for a richer view.
        </div>
      )}
    </div>
  )
}

/* ---------- Recent sessions list ---------- */

function RecentSessionsList({ sessions }: { sessions: SessionSummary[] }) {
  return (
    <div>
      {sessions.map((s) => (
        <Link
          key={s.sessionId}
          to={`/sessions/${s.sessionId}`}
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 12,
            padding: '12px 0',
            borderBottom: '1px solid var(--border-subtle)',
            textDecoration: 'none',
            color: 'inherit',
          }}
        >
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 14, fontWeight: 500, color: 'var(--text-heading)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {s.title || s.sessionId}
            </div>
            <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
              {s.project ?? '—'} · {s.messageCount} messages
            </div>
          </div>
          <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>
            {new Date(s.updatedAt).toLocaleString()}
          </div>
        </Link>
      ))}
    </div>
  )
}

/* ---------- Active Repositories ---------- */

function ActiveRepositoriesCard({ repos }: { repos: CodeRepoSummary[] | null }) {
  return (
    <div className="card" style={{ margin: 0 }}>
      <div className="card-header">
        <h2 style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ color: 'var(--primary)' }}><IconRepo size={18} /></span>
          Active Repositories
        </h2>
      </div>
      {repos === null ? (
        <div style={{ color: 'var(--text-muted)', fontSize: 13 }}>Loading…</div>
      ) : repos.length === 0 ? (
        <div style={{
          border: '1px dashed var(--border-strong)',
          borderRadius: 8,
          padding: '16px 12px',
          textAlign: 'center',
          color: 'var(--text-muted)',
          fontSize: 13,
        }}>
          No repositories linked
        </div>
      ) : (
        <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 6 }}>
          {repos.slice(0, 5).map((r) => (
            <li key={r.repositoryId}>
              <Link
                to={`/repos/${r.repositoryId}`}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '6px 8px',
                  borderRadius: 6,
                  textDecoration: 'none',
                  color: 'var(--text-body)',
                  fontSize: 13,
                }}
              >
                <span style={{ flex: 1, color: 'var(--text-heading)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {r.name}
                </span>
                <span style={{ color: 'var(--text-muted)', fontSize: 12 }}>
                  {r.fileCount} files
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/* ---------- Network Distribution ---------- */

function NetworkDistributionCard({ dist, pairings }: { dist: DistributedStatus | null; pairings: PairingRow[] | null }) {
  const summary = useMemo(() => {
    if (!dist) return { health: 'unknown' as const, label: 'Loading' }
    if (!dist.enabled) return { health: 'idle' as const, label: 'Not enabled' }
    if (!pairings || pairings.length === 0) return { health: 'idle' as const, label: 'No paired hosts' }
    const now = Date.now()
    const stale = pairings.filter((p) => {
      if (!p.lastContactAt) return true
      return now - new Date(p.lastContactAt).getTime() > 10 * 60 * 1000
    })
    if (stale.length === 0) return { health: 'operational' as const, label: 'Operational' }
    if (stale.length === pairings.length) return { health: 'stopped' as const, label: 'All hosts stale' }
    return { health: 'pending' as const, label: 'Degraded' }
  }, [dist, pairings])

  return (
    <div className="card" style={{ margin: 0 }}>
      <div className="card-header">
        <h2 style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{ color: 'var(--primary)' }}><IconDistributed size={18} /></span>
          Network Distribution
        </h2>
        <span className={`badge ${summary.health === 'operational' ? 'operational' :
          summary.health === 'pending' ? 'pending' :
          summary.health === 'stopped' ? 'stopped' : 'unknown'}`}>
          {summary.label}
        </span>
      </div>

      {/* Sparkline of host activity — one cell per paired host */}
      {pairings && pairings.length > 0 ? (
        <div style={{ display: 'flex', gap: 4 }}>
          {pairings.map((p) => {
            const fresh = p.lastContactAt
              ? Date.now() - new Date(p.lastContactAt).getTime() < 10 * 60 * 1000
              : false
            return (
              <div
                key={p.pairingId}
                title={`${p.friendlyName} — ${p.lastContactAt ? `last seen ${new Date(p.lastContactAt).toLocaleString()}` : 'never seen'}`}
                style={{
                  flex: 1,
                  height: 8,
                  borderRadius: 4,
                  background: fresh ? 'var(--primary)' : 'var(--border-strong)',
                  opacity: fresh ? 1 : 0.5,
                }}
              />
            )
          })}
        </div>
      ) : (
        <div style={{
          height: 8,
          background: 'var(--border-subtle)',
          borderRadius: 4,
        }} />
      )}

      <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 12 }}>
        {summary.health === 'operational'
          ? `System performing within nominal parameters. ${pairings?.length ?? 0} ${pairings?.length === 1 ? 'node' : 'nodes'} active in distributed pool.`
          : summary.health === 'pending'
          ? `Some paired hosts haven't checked in recently (>10min). Review pairings.`
          : summary.health === 'stopped'
          ? `Distributed mode enabled but no host has reported in. Check ingestor services on remote machines.`
          : dist?.enabled
          ? 'No remote ingestors paired yet.'
          : 'Distributed mode is disabled. Enable it on the Distributed page to allow remote ingestors.'}
      </div>
    </div>
  )
}
