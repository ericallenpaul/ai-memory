import type { ButtonHTMLAttributes, ReactNode } from 'react'

interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  children: ReactNode
  ariaLabel: string
}

/** Top-bar icon affordance — 36px square ghost button. Matches the mockup's right-aligned icon row. */
export default function IconButton({ children, ariaLabel, style, ...rest }: IconButtonProps) {
  return (
    <button
      aria-label={ariaLabel}
      {...rest}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        width: 36,
        height: 36,
        borderRadius: 8,
        background: 'transparent',
        border: '1px solid transparent',
        color: 'var(--text-muted)',
        cursor: 'pointer',
        transition: 'background-color 150ms ease, color 150ms ease, border-color 150ms ease',
        ...style,
      }}
      onMouseEnter={(e) => {
        e.currentTarget.style.background = 'var(--surface-elevated)'
        e.currentTarget.style.color = 'var(--text-heading)'
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.background = 'transparent'
        e.currentTarget.style.color = 'var(--text-muted)'
      }}
    >
      {children}
    </button>
  )
}
