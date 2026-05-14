import type { ReactNode } from 'react'

interface EmptyStateProps {
  icon?: ReactNode
  title: string
  description?: ReactNode
  action?: ReactNode
}

/** Centered empty state matching the mockup: icon circle, headline, supporting copy, optional CTA. */
export default function EmptyState({ icon, title, description, action }: EmptyStateProps) {
  return (
    <div className="empty-state">
      {icon && <div className="icon">{icon}</div>}
      <div className="title">{title}</div>
      {description && <div className="description">{description}</div>}
      {action}
    </div>
  )
}
