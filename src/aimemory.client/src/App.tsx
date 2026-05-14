import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { useEffect, useState } from 'react'
import { getSetupStatus, getMe } from './api/client'
import { useTheme } from './ThemeContext'
import { ApiProvider } from './api/ApiContext'
import Layout from './components/Layout'
import SetupWizard from './pages/SetupWizard'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Sessions from './pages/Sessions'
import SessionDetail from './pages/SessionDetail'
import Search from './pages/Search'
import Logs from './pages/Logs'
import ApiKeys from './pages/ApiKeys'
import Repos from './pages/Repos'
import RepoDetail from './pages/RepoDetail'
import Services from './pages/Services'
import Distributed from './pages/Distributed'
import DbBrowser from './pages/DbBrowser'
import Settings from './pages/Settings'
import Logo from './components/Logo'
import Spinner from './components/Spinner'

type AppState = 'loading' | 'setup' | 'login' | 'ready'

export default function App() {
  const [state, setState] = useState<AppState>('loading')
  const [username, setUsername] = useState('')

  useEffect(() => { checkState() }, [])

  async function checkState() {
    try {
      const { needsSetup } = await getSetupStatus()
      if (needsSetup) { setState('setup'); return }
      const user = await getMe()
      setUsername(user.username)
      setState('ready')
    } catch {
      setState('login')
    }
  }

  if (state === 'loading') return <LoadingScreen />
  if (state === 'setup') return (
    <BrowserRouter>
      <Routes>
        <Route path="*" element={<SetupWizard onComplete={() => setState('login')} />} />
      </Routes>
    </BrowserRouter>
  )
  if (state === 'login') return (
    <BrowserRouter>
      <Routes>
        <Route path="*" element={<Login onLogin={(u) => { setUsername(u); setState('ready') }} />} />
      </Routes>
    </BrowserRouter>
  )

  return (
    <ApiProvider>
      <BrowserRouter>
        <Routes>
          <Route element={<Layout username={username} onLogout={() => setState('login')} />}>
            <Route path="/" element={<Dashboard />} />
            <Route path="/sessions" element={<Sessions />} />
            <Route path="/sessions/:id" element={<SessionDetail />} />
            <Route path="/search" element={<Search />} />
            <Route path="/logs" element={<Logs />} />
            <Route path="/keys" element={<ApiKeys />} />
            <Route path="/repos" element={<Repos />} />
            <Route path="/repos/:repoId" element={<RepoDetail />} />
            <Route path="/services" element={<Services />} />
            <Route path="/distributed" element={<Distributed />} />
            <Route path="/db" element={<DbBrowser />} />
            <Route path="/settings" element={<Settings />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </ApiProvider>
  )
}

function LoadingScreen() {
  const { colors } = useTheme()
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh' }}>
      <div style={{ textAlign: 'center', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '1.25rem' }}>
        <Logo size={96} />
        <div style={{ fontSize: '1.5rem', fontWeight: 700, color: colors.textPrimary, letterSpacing: '-0.01em' }}>AIMemory</div>
        <Spinner size={28} label="Loading application..." />
      </div>
    </div>
  )
}
