/**
 * Design tokens derived from DESIGN.md.
 *
 * Light is the default per the mockup. Dark mirrors the structure so the design
 * tracks across both themes via the same property names.
 *
 * Naming sticks with the legacy "bgDeep / bgCard / borderDefault / textPrimary"
 * shape so existing pages keep working; new pages should prefer the CSS variables
 * defined in index.css (var(--surface), var(--primary), etc.) for clarity.
 */
export interface ThemeColors {
  bgDeep: string         // canvas / app background (Slate White in light, Deep Navy in dark)
  bgCard: string         // card surface (Pure White in light, elevated navy in dark)
  bgElevated: string     // hover / pressed surfaces
  bgSidebar: string      // sidebar background — slightly darker than canvas in light
  bgCodeBlock: string
  bgHelpPanel: string
  borderDefault: string  // 1px card outline
  borderSubtle: string   // table dividers, sidebar separator
  borderHelpPanel: string
  textPrimary: string    // Deep Navy in light; near-white in dark
  textSecondary: string
  textMuted: string      // Slate Gray in light
  textDim: string
  textBody: string
  accentPrimary: string  // Vibrant Blue #2563EB
  accentLight: string
  accentHover: string
  accentRing: string     // primary at 20% — focus glow per DESIGN.md
  errorBg: string
  errorText: string
  statusGreen: string
  statusRed: string
  statusAmber: string
  statusPurple: string
}

export const lightColors: ThemeColors = {
  bgDeep: '#f8fafc',          // slate-white canvas
  bgCard: '#ffffff',          // pure white card surface
  bgElevated: '#f1f5f9',      // hover / sidebar
  bgSidebar: '#f1f5f9',
  bgCodeBlock: '#f8fafc',
  bgHelpPanel: '#eff6ff',
  borderDefault: '#e2e8f0',   // 1px card border
  borderSubtle: '#f1f5f9',    // table row divider
  borderHelpPanel: '#bfdbfe',
  textPrimary: '#0f172a',     // deep navy headings
  textSecondary: '#1e293b',
  textMuted: '#64748b',       // slate gray
  textDim: '#94a3b8',
  textBody: '#334155',
  accentPrimary: '#2563eb',   // vibrant blue
  accentLight: '#3b82f6',
  accentHover: '#1d4ed8',
  accentRing: 'rgba(37, 99, 235, 0.2)',
  errorBg: '#fef2f2',
  errorText: '#b91c1c',
  statusGreen: '#059669',
  statusRed: '#dc2626',
  statusAmber: '#d97706',
  statusPurple: '#7c3aed',
}

export const darkColors: ThemeColors = {
  bgDeep: '#0f172a',
  bgCard: '#1e293b',
  bgElevated: '#334155',
  bgSidebar: '#0b1426',
  bgCodeBlock: '#020617',
  bgHelpPanel: '#0c1524',
  borderDefault: '#334155',
  borderSubtle: '#1e293b',
  borderHelpPanel: '#1e3a5f',
  textPrimary: '#f1f5f9',
  textSecondary: '#e2e8f0',
  textMuted: '#94a3b8',
  textDim: '#64748b',
  textBody: '#cbd5e1',
  accentPrimary: '#3b82f6',
  accentLight: '#60a5fa',
  accentHover: '#2563eb',
  accentRing: 'rgba(59, 130, 246, 0.25)',
  errorBg: '#7f1d1d',
  errorText: '#fca5a5',
  statusGreen: '#10b981',
  statusRed: '#ef4444',
  statusAmber: '#f59e0b',
  statusPurple: '#8b5cf6',
}

export type Theme = 'light' | 'dark'
