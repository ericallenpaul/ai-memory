import { useState } from 'react'
import { postLogin } from '../api/client'
import { useTheme } from '../ThemeContext'
import Logo from '../components/Logo'

export default function Login({ onLogin }: { onLogin: (username: string) => void }) {
  const { colors } = useTheme()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setError('')
    setSubmitting(true)
    try {
      const res = await postLogin({ username, password })
      onLogin(res.username)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Login failed')
    } finally {
      setSubmitting(false)
    }
  }

  const formStyle: React.CSSProperties = { background: colors.bgCard, borderRadius: 12, padding: '2rem', width: '100%', maxWidth: 380, border: `1px solid ${colors.borderDefault}` }
  const labelStyle: React.CSSProperties = { display: 'block', fontSize: '0.8rem', color: colors.textMuted, marginBottom: '0.25rem', marginTop: '0.75rem' }
  const inputStyle: React.CSSProperties = { width: '100%', padding: '0.5rem 0.75rem', background: colors.bgDeep, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textPrimary, fontSize: '0.875rem', marginBottom: '0.5rem' }
  const btnStyle: React.CSSProperties = { width: '100%', padding: '0.5rem', background: colors.accentPrimary, color: '#fff', border: 'none', borderRadius: 6, cursor: 'pointer', fontSize: '0.875rem', marginTop: '1rem' }
  const errorStyle: React.CSSProperties = { marginTop: '1rem', padding: '0.5rem', background: colors.errorBg, borderRadius: 6, fontSize: '0.8rem', color: colors.errorText }

  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '100vh' }}>
      <form onSubmit={handleSubmit} style={formStyle}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', marginBottom: '1rem' }}>
          <Logo size={300} />
          <p style={{ color: colors.textMuted, fontSize: '0.875rem' }}>Sign in to continue</p>
        </div>
        <label style={labelStyle}>Username</label>
        <input style={inputStyle} value={username} onChange={e => setUsername(e.target.value)} autoFocus />
        <label style={labelStyle}>Password</label>
        <input style={inputStyle} type="password" value={password} onChange={e => setPassword(e.target.value)} />
        <button type="submit" disabled={submitting} style={btnStyle}>
          {submitting ? 'Signing in...' : 'Sign in'}
        </button>
        {error && <div style={errorStyle}>{error}</div>}
      </form>
    </div>
  )
}
