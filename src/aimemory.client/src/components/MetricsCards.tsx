import { useTheme } from '../ThemeContext'

interface MetricCard {
  label: string
  value: string | number
  sub?: string
}

export default function MetricsCards({ cards }: { cards: MetricCard[] }) {
  const { colors } = useTheme()

  const cardStyle: React.CSSProperties = {
    background: colors.bgCard, borderRadius: 8, padding: '1rem 1.25rem', border: `1px solid ${colors.borderDefault}`
  }

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '1rem', marginBottom: '2rem' }}>
      {cards.map(card => (
        <div key={card.label} style={cardStyle}>
          <div style={{ fontSize: '0.75rem', color: colors.textMuted, textTransform: 'uppercase', letterSpacing: 1 }}>{card.label}</div>
          <div style={{ fontSize: '1.5rem', fontWeight: 700, color: colors.textPrimary, marginTop: '0.25rem' }}>{card.value}</div>
          {card.sub && <div style={{ fontSize: '0.75rem', color: colors.textDim, marginTop: '0.125rem' }}>{card.sub}</div>}
        </div>
      ))}
    </div>
  )
}
