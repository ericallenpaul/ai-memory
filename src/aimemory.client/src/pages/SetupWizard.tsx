import { useState } from 'react'
import { postSetupInit } from '../api/client'
import { useTheme } from '../ThemeContext'

type Step = 'welcome' | 'database' | 'port' | 'confirm'

export default function SetupWizard({ onComplete }: { onComplete: () => void }) {
  const { colors } = useTheme()
  const [step, setStep] = useState<Step>('welcome')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [dbProvider, setDbProvider] = useState('SQLite')
  const [pgHost, setPgHost] = useState('localhost')
  const [pgPort, setPgPort] = useState('5432')
  const [pgDatabase, setPgDatabase] = useState('aimemory')
  const [pgUsername, setPgUsername] = useState('aimemory_app')
  const [pgPassword, setPgPassword] = useState('')
  const [showPgHelp, setShowPgHelp] = useState(false)
  const [port, setPort] = useState('')
  const [error, setError] = useState('')
  const [submitting, setSubmitting] = useState(false)

  function buildConnectionString() {
    return `Host=${pgHost};Port=${pgPort};Database=${pgDatabase};Username=${pgUsername};Password=${pgPassword}`
  }

  async function handleSubmit() {
    setError('')
    setSubmitting(true)
    try {
      await postSetupInit({
        username, password, databaseProvider: dbProvider,
        connectionString: dbProvider === 'PostgreSQL' ? buildConnectionString() : undefined,
        port: port ? parseInt(port) : undefined
      })
      onComplete()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Setup failed')
    } finally {
      setSubmitting(false)
    }
  }

  const containerStyle: React.CSSProperties = { display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '100vh', padding: '2rem' }
  const cardStyle: React.CSSProperties = { background: colors.bgCard, borderRadius: 12, padding: '2rem', width: '100%', maxWidth: 520, border: `1px solid ${colors.borderDefault}` }
  const stepTitle: React.CSSProperties = { fontSize: '1rem', marginBottom: '1rem', color: colors.textSecondary }
  const labelStyle: React.CSSProperties = { display: 'block', fontSize: '0.8rem', color: colors.textMuted, marginBottom: '0.25rem', marginTop: '0.75rem' }
  const inputStyle: React.CSSProperties = { width: '100%', padding: '0.5rem 0.75rem', background: colors.bgDeep, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, color: colors.textPrimary, fontSize: '0.875rem', marginBottom: '0.75rem', boxSizing: 'border-box' }
  const btnStyle: React.CSSProperties = { padding: '0.5rem 1.5rem', background: colors.accentPrimary, color: '#fff', border: 'none', borderRadius: 6, cursor: 'pointer', fontSize: '0.875rem', marginTop: '0.5rem', flex: 1 }
  const radioBtnStyle: React.CSSProperties = { flex: 1, padding: '0.75rem', background: colors.bgDeep, border: '2px solid', borderRadius: 8, cursor: 'pointer', textAlign: 'left' as const, fontSize: '0.875rem' }
  const summaryStyle: React.CSSProperties = { background: colors.bgDeep, borderRadius: 6, padding: '1rem', marginBottom: '1rem', fontSize: '0.875rem', lineHeight: 1.8 }
  const errorStyle: React.CSSProperties = { marginTop: '1rem', padding: '0.5rem', background: colors.errorBg, borderRadius: 6, fontSize: '0.8rem', color: colors.errorText }

  return (
    <div style={containerStyle}>
      <div style={cardStyle}>
        <h1 style={{ fontSize: '1.5rem', marginBottom: '0.25rem' }}>AIMemory Setup</h1>
        <p style={{ color: colors.textMuted, marginBottom: '2rem', fontSize: '0.875rem' }}>Configure your instance</p>

        {step === 'welcome' && (
          <>
            <h2 style={stepTitle}>Step 1: Admin Account</h2>
            <label style={labelStyle}>Username</label>
            <input style={inputStyle} value={username} onChange={e => setUsername(e.target.value)} placeholder="admin" />
            <label style={labelStyle}>Password</label>
            <input style={inputStyle} type="password" value={password} onChange={e => setPassword(e.target.value)} placeholder="Min 8 characters" />
            <label style={labelStyle}>Confirm Password</label>
            <input style={inputStyle} type="password" value={confirmPassword} onChange={e => setConfirmPassword(e.target.value)} placeholder="Re-enter password" />
            <button style={btnStyle} onClick={() => {
              if (!username) { setError('Username is required'); return }
              if (password.length < 8) { setError('Password must be at least 8 characters'); return }
              if (password !== confirmPassword) { setError('Passwords do not match'); return }
              setError(''); setStep('database')
            }}>Next</button>
          </>
        )}

        {step === 'database' && (
          <>
            <h2 style={stepTitle}>Step 2: Database</h2>
            <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '1rem' }}>
              <button onClick={() => { setDbProvider('SQLite'); setShowPgHelp(false) }}
                style={{ ...radioBtnStyle, borderColor: dbProvider === 'SQLite' ? colors.accentPrimary : colors.borderDefault, color: dbProvider === 'SQLite' ? colors.textPrimary : colors.textMuted }}>
                <strong>SQLite</strong>
                <div style={{ fontSize: '0.75rem', marginTop: '0.25rem' }}>
                  Simple, zero-config, file-based
                </div>
              </button>
              <button onClick={() => setDbProvider('PostgreSQL')}
                style={{ ...radioBtnStyle, borderColor: dbProvider === 'PostgreSQL' ? colors.accentPrimary : colors.borderDefault, color: dbProvider === 'PostgreSQL' ? colors.textPrimary : colors.textMuted }}>
                <strong>PostgreSQL</strong>
                <div style={{ fontSize: '0.75rem', marginTop: '0.25rem' }}>
                  Better for heavy or concurrent usage
                </div>
              </button>
            </div>

            {dbProvider === 'PostgreSQL' && (
              <>
                <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: '0.5rem' }}>
                  <button style={{ background: 'none', border: 'none', color: colors.accentLight, fontSize: '0.78rem', cursor: 'pointer', textDecoration: 'underline', padding: 0 }} onClick={() => setShowPgHelp(!showPgHelp)}>
                    {showPgHelp ? 'Hide setup help' : 'Need help setting up PostgreSQL?'}
                  </button>
                </div>

                {showPgHelp && <PgHelpPanel pgDatabase={pgDatabase} pgUsername={pgUsername} />}

                <div style={{ display: 'flex', gap: '0.75rem' }}>
                  <div style={{ flex: 3 }}>
                    <label style={labelStyle}>Host</label>
                    <input style={inputStyle} value={pgHost} onChange={e => setPgHost(e.target.value)} placeholder="localhost" />
                  </div>
                  <div style={{ flex: 1 }}>
                    <label style={labelStyle}>Port</label>
                    <input style={inputStyle} value={pgPort} onChange={e => setPgPort(e.target.value)} placeholder="5432" />
                  </div>
                </div>
                <label style={labelStyle}>Database</label>
                <input style={inputStyle} value={pgDatabase} onChange={e => setPgDatabase(e.target.value)} placeholder="aimemory" />
                <div style={{ display: 'flex', gap: '0.75rem' }}>
                  <div style={{ flex: 1 }}>
                    <label style={labelStyle}>Username</label>
                    <input style={inputStyle} value={pgUsername} onChange={e => setPgUsername(e.target.value)} placeholder="aimemory_app" />
                  </div>
                  <div style={{ flex: 1 }}>
                    <label style={labelStyle}>Password</label>
                    <input style={inputStyle} type="password" value={pgPassword} onChange={e => setPgPassword(e.target.value)} placeholder="Database password" />
                  </div>
                </div>
              </>
            )}

            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button style={{ ...btnStyle, background: colors.bgElevated }} onClick={() => { setStep('welcome'); setShowPgHelp(false) }}>Back</button>
              <button style={btnStyle} onClick={() => {
                if (dbProvider === 'PostgreSQL') {
                  if (!pgHost) { setError('Host is required'); return }
                  if (!pgDatabase) { setError('Database name is required'); return }
                  if (!pgUsername) { setError('Username is required'); return }
                  if (!pgPassword) { setError('Password is required'); return }
                }
                setError(''); setShowPgHelp(false); setStep('port')
              }}>Next</button>
            </div>
          </>
        )}

        {step === 'port' && (
          <>
            <h2 style={stepTitle}>Step 3: Port</h2>
            <label style={labelStyle}>API / Web UI Port (leave blank for auto)</label>
            <input style={inputStyle} value={port} onChange={e => setPort(e.target.value)} placeholder="e.g. 5219" type="number" />
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button style={{ ...btnStyle, background: colors.bgElevated }} onClick={() => setStep('database')}>Back</button>
              <button style={btnStyle} onClick={() => setStep('confirm')}>Next</button>
            </div>
          </>
        )}

        {step === 'confirm' && (
          <>
            <h2 style={stepTitle}>Step 4: Confirm</h2>
            <div style={summaryStyle}>
              <div><strong>Admin:</strong> {username}</div>
              <div><strong>Database:</strong> {dbProvider}</div>
              {dbProvider === 'PostgreSQL' && (
                <>
                  <div><strong>Host:</strong> {pgHost}:{pgPort}</div>
                  <div><strong>Database:</strong> {pgDatabase}</div>
                  <div><strong>User:</strong> {pgUsername}</div>
                </>
              )}
              <div><strong>Port:</strong> {port || '(auto)'}</div>
            </div>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <button style={{ ...btnStyle, background: colors.bgElevated }} onClick={() => setStep('port')}>Back</button>
              <button style={btnStyle} onClick={handleSubmit} disabled={submitting}>
                {submitting ? 'Initializing...' : 'Initialize'}
              </button>
            </div>
          </>
        )}

        {error && <div style={errorStyle}>{error}</div>}
      </div>
    </div>
  )
}

function PgHelpPanel({ pgDatabase, pgUsername }: { pgDatabase: string; pgUsername: string }) {
  const { colors } = useTheme()
  const [copied, setCopied] = useState(false)

  const script = `-- Run this in pgAdmin (Query Tool) or psql connected as a superuser

-- 1. Create the database
CREATE DATABASE ${pgDatabase || 'aimemory'};

-- 2. Create the application user
CREATE USER ${pgUsername || 'aimemory_app'} WITH PASSWORD 'your_password_here';

-- 3. Grant permissions
GRANT ALL PRIVILEGES ON DATABASE ${pgDatabase || 'aimemory'} TO ${pgUsername || 'aimemory_app'};

-- 4. Connect to the new database and grant schema permissions
\\c ${pgDatabase || 'aimemory'}
GRANT ALL ON SCHEMA public TO ${pgUsername || 'aimemory_app'};
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO ${pgUsername || 'aimemory_app'};
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO ${pgUsername || 'aimemory_app'};`

  const helpPanelStyle: React.CSSProperties = { background: colors.bgHelpPanel, border: `1px solid ${colors.borderHelpPanel}`, borderRadius: 8, padding: '1rem', marginBottom: '1rem' }
  const preStyle: React.CSSProperties = { background: colors.bgCodeBlock, border: `1px solid ${colors.borderDefault}`, borderRadius: 6, padding: '0.75rem', fontSize: '0.72rem', lineHeight: 1.6, color: colors.textMuted, overflow: 'auto', maxHeight: '200px', margin: 0, whiteSpace: 'pre-wrap' }
  const copyBtnStyle: React.CSSProperties = { position: 'absolute' as const, top: 6, right: 6, background: colors.bgElevated, border: 'none', borderRadius: 4, color: colors.textSecondary, fontSize: '0.7rem', padding: '0.2rem 0.5rem', cursor: 'pointer', zIndex: 1 }
  const codeInlineStyle: React.CSSProperties = { background: colors.bgElevated, padding: '0.1rem 0.3rem', borderRadius: 3, fontSize: '0.75rem' }

  return (
    <div style={helpPanelStyle}>
      <div style={{ fontSize: '0.8rem', fontWeight: 600, marginBottom: '0.5rem', color: colors.textSecondary }}>
        PostgreSQL Setup
      </div>
      <ol style={{ margin: 0, paddingLeft: '1.25rem', fontSize: '0.78rem', lineHeight: 1.7, color: colors.textBody }}>
        <li>Install PostgreSQL if you haven't already</li>
        <li>Open <strong>pgAdmin</strong> or connect via <strong>psql</strong></li>
        <li>Run the script below as a superuser (e.g., <code style={codeInlineStyle}>postgres</code>)</li>
        <li>Fill in the fields below to match the values you used</li>
      </ol>
      <div style={{ position: 'relative', marginTop: '0.75rem' }}>
        <button
          style={copyBtnStyle}
          onClick={() => {
            navigator.clipboard.writeText(script)
            setCopied(true)
            setTimeout(() => setCopied(false), 2000)
          }}
        >
          {copied ? 'Copied!' : 'Copy'}
        </button>
        <pre style={preStyle}>{script}</pre>
      </div>
    </div>
  )
}
