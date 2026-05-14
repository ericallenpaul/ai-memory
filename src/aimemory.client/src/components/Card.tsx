import type { CSSProperties, ReactNode } from 'react'

interface CardProps {
  title?: ReactNode
  action?: ReactNode
  children: ReactNode
  /** When true, removes the inner padding so a child (e.g. a table) can fill edge-to-edge. */
  flush?: boolean
  style?: CSSProperties
}

/**
 * Card matching DESIGN.md: pure white surface, 1px border, 12px radius, 24px padding,
 * optional header strip with title + right-aligned action.
 */
export default function Card({ title, action, children, flush, style }: CardProps) {
  return (
    <div className={`card${flush ? ' flush' : ''}`} style={style}>
      {(title || action) && (
        <div className="card-header" style={flush ? { padding: '20px 24px', marginBottom: 0 } : undefined}>
          {title && <h2>{title}</h2>}
          {action}
        </div>
      )}
      {children}
    </div>
  )
}
