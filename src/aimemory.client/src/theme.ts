export interface ThemeColors {
  bgDeep: string
  bgCard: string
  bgElevated: string
  bgCodeBlock: string
  bgHelpPanel: string
  borderDefault: string
  borderSubtle: string
  borderHelpPanel: string
  textPrimary: string
  textSecondary: string
  textMuted: string
  textDim: string
  textBody: string
  accentPrimary: string
  accentLight: string
  accentHover: string
  errorBg: string
  errorText: string
  statusGreen: string
  statusRed: string
  statusAmber: string
  statusPurple: string
}

export const darkColors: ThemeColors = {
  bgDeep: '#0f172a',
  bgCard: '#1e293b',
  bgElevated: '#334155',
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
  errorBg: '#7f1d1d',
  errorText: '#fca5a5',
  statusGreen: '#10b981',
  statusRed: '#ef4444',
  statusAmber: '#f59e0b',
  statusPurple: '#8b5cf6',
}

export const lightColors: ThemeColors = {
  bgDeep: '#ffffff',
  bgCard: '#fcfdfd',
  bgElevated: '#e2e8f0',
  bgCodeBlock: '#f8fafc',
  bgHelpPanel: '#eff6ff',
  borderDefault: '#cbd5e1',
  borderSubtle: '#e2e8f0',
  borderHelpPanel: '#bfdbfe',
  textPrimary: '#0f172a',
  textSecondary: '#1e293b',
  textMuted: '#64748b',
  textDim: '#94a3b8',
  textBody: '#334155',
  accentPrimary: '#3b82f6',
  accentLight: '#2563eb',
  accentHover: '#1d4ed8',
  errorBg: '#fef2f2',
  errorText: '#dc2626',
  statusGreen: '#059669',
  statusRed: '#dc2626',
  statusAmber: '#d97706',
  statusPurple: '#7c3aed',
}

export type Theme = 'dark' | 'light'
