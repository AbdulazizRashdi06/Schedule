import { useState, type ReactNode } from 'react'
import { Link, Navigate, useNavigate } from 'react-router-dom'
import { ApiError, api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Button, TextField, cx, useShake } from '../components/ui'

/** Staff sign-in and change password: navy panel on one side, the form on the other. */
export function SplitPage({ kicker, title, foot, size = 120, children }: { kicker: string; title: string; foot: ReactNode; size?: number; children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-wrap bg-cream text-navy">
      <section className="flex flex-[1_1_420px] flex-col justify-between gap-10 bg-navy p-8 text-cream sm:p-14">
        <span className="label text-[13px] tracking-[0.14em] text-ice">{kicker}</span>
        <h1 className="display leading-[0.84] tracking-[-0.06em] text-sky" style={{ fontSize: `clamp(64px, 9vw, ${size}px)` }}>
          {title}
        </h1>
        <span className="text-[14px] font-semibold text-ice">{foot}</span>
      </section>
      <section className="flex flex-[1_1_420px] items-center p-8 sm:p-14">{children}</section>
    </div>
  )
}

export default function AdminLogin() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [show, setShow] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  // Set while we navigate ourselves, so the signed-in redirect below doesn't drop the temporary password.
  const [entering, setEntering] = useState(false)
  const { shakeClass, shake } = useShake()

  if (!entering && session?.kind === 'admin') return <Navigate to={session.mustChangePassword ? '/admin/password' : '/admin'} replace />

  return (
    <SplitPage kicker="Uni Schedule" title="Staff." foot="Lost access? Ask an owner to reset your password.">
      <form
        noValidate
        className={cx('flex w-full max-w-[420px] flex-col gap-5', shakeClass)}
        onSubmit={async (e) => {
          e.preventDefault()
          if (busy) return
          if (!username.trim() || !password) {
            setError('Enter your username and password.')
            shake()
            return
          }
          setBusy(true)
          setError('')
          try {
            const r = await api.post<{ mustChangePassword: boolean }>('/auth/admin-login', { username, password })
            setEntering(true)
            await refresh()
            // The temporary password is the "current" one on the next screen, so it isn't asked for twice.
            navigate(r.mustChangePassword ? '/admin/password' : '/admin', { replace: true, state: r.mustChangePassword ? { current: password } : undefined })
          } catch (err) {
            setError(
              err instanceof ApiError && err.status === 401
                ? 'That username and password don’t match.'
                : err instanceof ApiError && err.status === 429
                  ? 'Too many tries. This account is locked for 15 minutes.'
                  : errorText(err),
            )
            shake()
            setBusy(false)
          }
        }}
      >
        <h2 className="text-[40px] font-extrabold tracking-[-0.03em]">Sign in</h2>
        <TextField
          label="Username"
          autoFocus
          autoComplete="username"
          autoCapitalize="none"
          value={username}
          invalid={!!error}
          aria-describedby="login-msg"
          onChange={(e) => {
            setUsername(e.target.value)
            setError('')
          }}
          boxClassName="h-14 rounded-2xl"
          className="[&_input]:text-[18px] [&_label]:text-[12px]"
        />
        <TextField
          label="Password"
          type={show ? 'text' : 'password'}
          autoComplete="current-password"
          value={password}
          invalid={!!error}
          aria-describedby="login-msg"
          onChange={(e) => {
            setPassword(e.target.value)
            setError('')
          }}
          boxClassName="h-14 rounded-2xl pr-2"
          className="[&_input]:text-[18px] [&_label]:text-[12px]"
          trailing={
            <button type="button" aria-pressed={show} onClick={() => setShow(!show)} className="press label h-10 shrink-0 rounded-full bg-navy/[0.07] px-3 text-[11px]">
              {show ? 'Hide' : 'Show'}
            </button>
          }
        />
        <div id="login-msg" aria-live="polite" className="min-h-6">
          {error && <span className="msg block text-[15px] font-bold text-alert">{error}</span>}
        </div>
        <Button type="submit" className="h-14 text-[17px]" busy={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>
        <p className="text-center text-[14px] font-semibold text-muted">
          Owners and students sign in with their{' '}
          <Link to="/login" className="text-navy underline underline-offset-2">
            university ID
          </Link>
          .
        </p>
      </form>
    </SplitPage>
  )
}
