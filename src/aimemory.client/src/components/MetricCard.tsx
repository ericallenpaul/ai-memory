import type { ReactNode } from 'react'

/**
 * Dashboard tile: uppercase tracked label on top, big numeral, optional subtext.
 * Pure presentation — feed values from /api/stats.
 */
interface MetricCardProps {
  label: string
  value: ReactNode
  subtext?: ReactNode
}

export default function MetricCard({ label, value, subtext }: MetricCardProps) {
  return (
    <div className="card" style={{ margin: 0, padding: '20px 22px' }}>
      <div className="label" style={{ marginBottom: 12, color: 'var(--text-muted)' }}>
        {label}
      </div>
      <div style={{
        fontSize: 32,
        fontWeight: 600,
        lineHeight: 1.1,
        color: 'var(--text-heading)',
        letterSpacing: '-0.01em',
      }}>
        {value}
      </div>
      {subtext && (
        <div style={{ fontSize: 12, color: 'var(--text-muted)', marginTop: 6 }}>
          {subtext}
        </div>
      )}
    </div>
  )
}
