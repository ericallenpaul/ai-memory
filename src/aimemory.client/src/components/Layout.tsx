import { Outlet, NavLink } from 'react-router-dom'
import { postLogout } from '../api/client'
import { useTheme } from '../ThemeContext'
import Logo from './Logo'

const navItems = [
  { to: '/', label: 'Dashboard' },
  { to: '/sessions', label: 'Sessions' },
  { to: '/search', label: 'Search' },
  { to: '/logs', label: 'Logs' },
  { to: '/keys', label: 'API Keys' },
]

export default function Layout({ username, onLogout }: { username: string; onLogout: () => void }) {
  const { theme, colors, toggleTheme } = useTheme()

  async function handleLogout() {
    await postLogout().catch(() => {})
    onLogout()
  }

  const sidebarStyle: React.CSSProperties = {
    width: 320, background: colors.bgDeep, borderRight: `1px solid ${colors.borderSubtle}`,
    display: 'flex', flexDirection: 'column'
  }
  const logoutBtnStyle: React.CSSProperties = {
    background: 'none', border: `1px solid ${colors.borderDefault}`, color: colors.textMuted,
    padding: '0.375rem 0.75rem', borderRadius: 4, cursor: 'pointer', fontSize: '0.8rem', width: '100%'
  }
  const toggleBtnStyle: React.CSSProperties = {
    background: 'none', border: `1px solid ${colors.borderDefault}`, color: colors.textMuted,
    padding: '0.375rem 0.75rem', borderRadius: 4, cursor: 'pointer', fontSize: '0.85rem',
    width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '0.4rem'
  }

  return (
    <div style={{ display: 'flex', minHeight: '100vh' }}>
      <nav style={sidebarStyle}>
        <div style={{ padding: '1rem', borderBottom: `1px solid ${colors.borderSubtle}`, display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
          <Logo size={300} />
        </div>
        <div style={{ padding: '0.5rem', flex: 1 }}>
          {navItems.map(item => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.to === '/'}
              className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
            >
              {item.label}
            </NavLink>
          ))}
        </div>
        <div style={{ padding: '0 1rem 0.5rem', textAlign: 'center' }}>
          <div style={{ fontSize: '0.7rem', color: colors.textDim }}>LLM Work Ledger</div>
        </div>
        <div style={{ padding: '0 1rem 0.5rem' }}>
          <button onClick={toggleTheme} style={toggleBtnStyle}>
            {theme === 'dark' ? '\u2600\uFE0F' : '\uD83C\uDF19'}{' '}
            {theme === 'dark' ? 'Light mode' : 'Dark mode'}
          </button>
        </div>
        <div style={{ padding: '0.5rem 1rem 1rem', borderTop: `1px solid ${colors.borderSubtle}` }}>
          <div style={{ fontSize: '0.875rem', color: colors.textMuted, marginBottom: '0.5rem' }}>{username}</div>
          <button onClick={handleLogout} style={logoutBtnStyle}>Log out</button>
        </div>
      </nav>
      <main style={{ flex: 1, padding: '2rem', overflowY: 'auto' }}>
        <Outlet />
      </main>
    </div>
  )
}
