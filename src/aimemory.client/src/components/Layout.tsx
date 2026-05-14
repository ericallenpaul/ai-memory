import { Outlet, NavLink, useLocation } from 'react-router-dom'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { postLogout } from '../api/client'
import { useTheme } from '../ThemeContext'
import Logo from './Logo'
import IconButton from './IconButton'
import {
  IconDashboard, IconSessions, IconSearch, IconLogs, IconKey,
  IconRepo, IconService, IconDistributed, IconDb,
  IconSettings, IconBell, IconUser, IconSun, IconMoon,
} from './Icons'

interface NavItem {
  to: string
  label: string
  icon: ReactNode
}

const mainNav: NavItem[] = [
  { to: '/', label: 'Dashboard', icon: <IconDashboard /> },
  { to: '/sessions', label: 'Sessions', icon: <IconSessions /> },
  { to: '/search', label: 'Search', icon: <IconSearch /> },
  { to: '/logs', label: 'Logs', icon: <IconLogs /> },
  { to: '/keys', label: 'API Keys', icon: <IconKey /> },
]

const adminNav: NavItem[] = [
  { to: '/repos', label: 'Repositories', icon: <IconRepo /> },
  { to: '/services', label: 'Services', icon: <IconService /> },
  { to: '/distributed', label: 'Distributed', icon: <IconDistributed /> },
  { to: '/db', label: 'DB Browser', icon: <IconDb /> },
]

// Page-title lookup so the top bar can label the current view without each
// page having to render its own h1. Pages can still render their own headers;
// this is just the fallback.
const pageTitles: Record<string, string> = {
  '/': 'Dashboard',
  '/sessions': 'Sessions',
  '/search': 'Search',
  '/logs': 'Logs',
  '/keys': 'API Keys',
  '/repos': 'Repositories',
  '/services': 'Services',
  '/distributed': 'Distributed',
  '/db': 'Database Browser',
  '/settings': 'Settings',
}

function currentTitle(pathname: string): string {
  if (pageTitles[pathname]) return pageTitles[pathname]
  if (pathname.startsWith('/sessions/')) return 'Session detail'
  if (pathname.startsWith('/repos/')) return 'Repository detail'
  return 'AIMemory'
}

export default function Layout({ username, onLogout }: { username: string; onLogout: () => void }) {
  const { theme, toggleTheme } = useTheme()
  const location = useLocation()

  async function handleLogout() {
    await postLogout().catch(() => {})
    onLogout()
  }

  return (
    <div style={{ display: 'flex', minHeight: '100vh', background: 'var(--surface-canvas)' }}>
      <Sidebar />
      <main style={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <TopBar
          title={currentTitle(location.pathname)}
          theme={theme}
          onToggleTheme={toggleTheme}
          username={username}
          onLogout={handleLogout}
        />
        <div style={{
          padding: '24px 32px 48px',
          maxWidth: 1400,
          width: '100%',
          margin: '0 auto',
          flex: 1,
        }}>
          <Outlet />
        </div>
      </main>
    </div>
  )
}

function Sidebar() {
  return (
    <nav
      style={{
        width: 'var(--sidebar-width)',
        flexShrink: 0,
        background: 'var(--surface-elevated)',
        borderRight: '1px solid var(--border-subtle)',
        display: 'flex',
        flexDirection: 'column',
      }}
    >
      {/* Brand block */}
      <div style={{
        padding: '20px 16px',
        display: 'flex',
        alignItems: 'center',
        gap: 10,
      }}>
        <Logo size={36} />
        <div>
          <div style={{
            fontSize: 16,
            fontWeight: 700,
            color: 'var(--text-heading)',
            letterSpacing: '-0.01em',
          }}>
            AIMemory
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>LLM Work Ledger</div>
        </div>
      </div>

      {/* Nav groups */}
      <div style={{ padding: '8px 12px', flex: 1, overflowY: 'auto' }}>
        {mainNav.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.to === '/'}
            className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
          >
            <span style={{ display: 'inline-flex', width: 18 }}>{item.icon}</span>
            <span>{item.label}</span>
          </NavLink>
        ))}
        <div className="nav-group">Admin</div>
        {adminNav.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
          >
            <span style={{ display: 'inline-flex', width: 18 }}>{item.icon}</span>
            <span>{item.label}</span>
          </NavLink>
        ))}
      </div>

      {/* Pinned bottom: Settings only — identity + logout live in the top-bar UserMenu now */}
      <div style={{
        padding: '8px 12px 12px',
        borderTop: '1px solid var(--border-subtle)',
      }}>
        <NavLink
          to="/settings"
          className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
        >
          <span style={{ display: 'inline-flex', width: 18 }}><IconSettings /></span>
          <span>Settings</span>
        </NavLink>
      </div>
    </nav>
  )
}

interface TopBarProps {
  title: string
  theme: 'light' | 'dark'
  onToggleTheme: () => void
  username: string
  onLogout: () => void
}

function TopBar({ title, theme, onToggleTheme, username, onLogout }: TopBarProps) {
  return (
    <header style={{
      display: 'flex',
      alignItems: 'center',
      padding: '14px 32px',
      borderBottom: '1px solid var(--border-subtle)',
      background: 'var(--surface-canvas)',
      gap: 12,
    }}>
      <h1 style={{ flex: 1, fontSize: 22, fontWeight: 600, margin: 0 }}>{title}</h1>
      <IconButton ariaLabel={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`} onClick={onToggleTheme}>
        {theme === 'dark' ? <IconSun /> : <IconMoon />}
      </IconButton>
      <IconButton ariaLabel="Notifications">
        <IconBell />
      </IconButton>
      <UserMenu username={username} onLogout={onLogout} />
    </header>
  )
}

function UserMenu({ username, onLogout }: { username: string; onLogout: () => void }) {
  const [open, setOpen] = useState(false)
  const wrapperRef = useRef<HTMLDivElement>(null)

  // Close on outside click + on Escape so the menu doesn't strand.
  useEffect(() => {
    if (!open) return
    const onClick = (e: MouseEvent) => {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', onClick)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onClick)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const initial = username ? username[0].toUpperCase() : '?'

  return (
    <div ref={wrapperRef} style={{ position: 'relative' }}>
      <IconButton
        ariaLabel="Account menu"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="menu"
      >
        <IconUser />
      </IconButton>
      {open && (
        <div
          role="menu"
          style={{
            position: 'absolute',
            top: 'calc(100% + 8px)',
            right: 0,
            minWidth: 220,
            background: 'var(--surface)',
            border: '1px solid var(--border)',
            borderRadius: 10,
            boxShadow: '0 8px 24px rgba(15, 23, 42, 0.08)',
            padding: 6,
            zIndex: 100,
          }}
        >
          {/* Identity header */}
          <div style={{
            display: 'flex',
            alignItems: 'center',
            gap: 10,
            padding: '8px 10px 10px',
            borderBottom: '1px solid var(--border-subtle)',
            marginBottom: 6,
          }}>
            <div style={{
              width: 32, height: 32,
              borderRadius: '50%',
              background: 'var(--primary-soft)',
              color: 'var(--primary)',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              fontSize: 13,
              fontWeight: 600,
            }}>
              {initial}
            </div>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{
                fontSize: 13,
                fontWeight: 600,
                color: 'var(--text-heading)',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}>
                {username || 'guest'}
              </div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>Signed in</div>
            </div>
          </div>

          <button
            role="menuitem"
            onClick={() => { setOpen(false); onLogout() }}
            style={{
              display: 'flex',
              alignItems: 'center',
              width: '100%',
              padding: '8px 10px',
              background: 'transparent',
              border: 'none',
              borderRadius: 6,
              color: 'var(--text-body)',
              fontSize: 13,
              fontFamily: 'inherit',
              textAlign: 'left',
              cursor: 'pointer',
            }}
            onMouseEnter={(e) => { e.currentTarget.style.background = 'var(--surface-elevated)' }}
            onMouseLeave={(e) => { e.currentTarget.style.background = 'transparent' }}
          >
            Log out
          </button>
        </div>
      )}
    </div>
  )
}
