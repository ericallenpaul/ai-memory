/**
 * Inline SVG icons matching the mockup. Stroke-based, currentColor so they inherit
 * the surrounding text color. Sized 18px by default (consumer can override via props).
 */
type IconProps = { size?: number }

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.6,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
}

function svg({ size = 18, children }: IconProps & { children: React.ReactNode }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden>
      {children}
    </svg>
  )
}

export const IconDashboard = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <rect x="3" y="3" width="7.5" height="7.5" rx="1.5" />
    <rect x="13.5" y="3" width="7.5" height="4" rx="1.5" />
    <rect x="13.5" y="10" width="7.5" height="11" rx="1.5" />
    <rect x="3" y="13.5" width="7.5" height="7.5" rx="1.5" />
  </g>
)})

export const IconSessions = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <rect x="3" y="5" width="18" height="14" rx="2" />
    <path d="M7 9h10M7 13h7" />
  </g>
)})

export const IconSearch = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="11" cy="11" r="6" />
    <path d="m20 20-4.5-4.5" />
  </g>
)})

export const IconLogs = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M5 4h10l4 4v12a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Z" />
    <path d="M14 4v4h4M8 12h8M8 16h6" />
  </g>
)})

export const IconKey = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="8" cy="14" r="4" />
    <path d="m11 12 9-9M16 7l2 2" />
  </g>
)})

export const IconRepo = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M3 6a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6Z" />
  </g>
)})

export const IconService = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.6 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.6-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z" />
  </g>
)})

export const IconDistributed = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="12" cy="5" r="2" />
    <circle cx="5" cy="19" r="2" />
    <circle cx="19" cy="19" r="2" />
    <path d="M12 7v4M12 11l-5 6M12 11l5 6" />
  </g>
)})

export const IconDb = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <ellipse cx="12" cy="5" rx="8" ry="3" />
    <path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5" />
    <path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
  </g>
)})

export const IconSettings = (p: IconProps) => IconService(p)

export const IconBell = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M6 9a6 6 0 0 1 12 0v4l1.5 3h-15L6 13V9Z" />
    <path d="M10 19a2 2 0 0 0 4 0" />
  </g>
)})

export const IconUser = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="12" cy="8" r="4" />
    <path d="M4 21a8 8 0 0 1 16 0" />
  </g>
)})

export const IconMoon = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z" />
  </g>
)})

export const IconSun = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <circle cx="12" cy="12" r="4" />
    <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41" />
  </g>
)})

export const IconChat = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M21 12a8 8 0 0 1-8 8H7l-4 2 1-4.5A8 8 0 1 1 21 12Z" />
  </g>
)})

export const IconActivity = (p: IconProps) => svg({ ...p, children: (
  <g {...stroke}>
    <path d="M3 12h4l3-7 4 14 3-7h4" />
  </g>
)})
