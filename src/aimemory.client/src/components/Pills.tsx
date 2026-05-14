import type { ReactNode } from 'react'

interface PillTabsProps<T extends string> {
  value: T
  options: { value: T; label: ReactNode }[]
  onChange: (v: T) => void
}

/**
 * Pill-tab segmented control used by the Last-30-Days card (Usage / Cost toggle in the mockup).
 * Renders as a tight group of pill buttons; the active one fills with the primary tint.
 */
export function PillTabs<T extends string>({ value, options, onChange }: PillTabsProps<T>) {
  return (
    <div
      style={{
        display: 'inline-flex',
        gap: 4,
        padding: 4,
        background: 'var(--surface-elevated)',
        borderRadius: 999,
      }}
    >
      {options.map((opt) => {
        const active = opt.value === value
        return (
          <button
            key={opt.value}
            onClick={() => onChange(opt.value)}
            style={{
              padding: '5px 14px',
              fontSize: 13,
              fontWeight: 500,
              borderRadius: 999,
              border: 'none',
              cursor: 'pointer',
              background: active ? 'var(--primary)' : 'transparent',
              color: active ? 'var(--on-primary)' : 'var(--text-muted)',
            }}
          >
            {opt.label}
          </button>
        )
      })}
    </div>
  )
}
