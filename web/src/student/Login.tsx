import { useEffect, useRef, useState } from 'react'
import { Link, Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { BackIcon, Button, ErrorText, Input, Notice, ThemeToggle } from '../components/ui'

interface CodeSent {
  email: string
  resendAfterSeconds: number
  testCode: string | null
}

export default function Login() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const [id, setId] = useState('')
  const [sent, setSent] = useState<CodeSent | null>(null)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [wait, setWait] = useState(0)
  const codeRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (wait <= 0) return
    const t = setTimeout(() => setWait((w) => w - 1), 1000)
    return () => clearTimeout(t)
  }, [wait])

  if (session?.kind === 'student') return <Navigate to="/" replace />
  if (session?.kind === 'admin') return <Navigate to="/admin" replace />

  async function sendCode(e?: React.FormEvent) {
    e?.preventDefault()
    setBusy(true)
    setError('')
    try {
      const r = await api.post<CodeSent>('/auth/request-code', { universityId: id })
      setSent(r)
      setCode('')
      setWait(r.resendAfterSeconds)
      setTimeout(() => codeRef.current?.focus(), 50)
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  async function verify(value = code) {
    if (value.length !== 6) return
    setBusy(true)
    setError('')
    try {
      await api.post('/auth/verify', { universityId: id, code: value })
      await refresh()
      navigate('/', { replace: true })
    } catch (err) {
      setError(errorText(err))
      setBusy(false)
    }
  }

  return (
    <div className="mx-auto flex min-h-dvh max-w-sm flex-col px-5 pt-[max(1rem,env(safe-area-inset-top))]">
      <div className="flex justify-end">
        <ThemeToggle />
      </div>
      <div className="flex flex-1 flex-col justify-center pb-24">
        <div className="mb-8 space-y-2">
          <div className="flex size-12 items-center justify-center rounded-2xl bg-indigo-600 text-white">
            <svg viewBox="0 0 24 24" className="size-7" fill="none" stroke="currentColor" strokeWidth="1.8">
              <rect x="4" y="6" width="16" height="14" rx="2" />
              <path d="M4 10.5h16M9 3.5v4M15 3.5v4" strokeLinecap="round" />
            </svg>
          </div>
          <h1 className="text-2xl font-semibold tracking-tight">{sent ? 'Check your email' : 'Uni Schedule'}</h1>
          <p className="text-[15px] text-zinc-500">
            {sent ? (
              <>
                We sent a 6-digit code to <span className="font-medium text-zinc-800 dark:text-zinc-200">{sent.email}</span>
              </>
            ) : (
              'Your class timetable, always with you. Sign in with your university ID.'
            )}
          </p>
        </div>

        {!sent ? (
          <form onSubmit={sendCode} className="space-y-4">
            <Input
              autoFocus
              inputMode="numeric"
              autoComplete="username"
              placeholder="University ID, e.g. 24-0161"
              value={id}
              onChange={(e) => setId(e.target.value)}
              aria-label="University ID"
            />
            <ErrorText>{error}</ErrorText>
            <Button className="w-full" busy={busy} disabled={id.replace(/\D/g, '').length !== 6}>
              Send code
            </Button>
          </form>
        ) : (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              verify()
            }}
            className="space-y-4"
          >
            {sent.testCode && (
              <Notice tone="warn">
                Test mode: your code is <span className="font-mono font-semibold tracking-widest">{sent.testCode}</span>
              </Notice>
            )}
            <Input
              ref={codeRef}
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              placeholder="••••••"
              className="text-center font-mono text-2xl tracking-[0.5em]"
              value={code}
              aria-label="6-digit code"
              onChange={(e) => {
                const v = e.target.value.replace(/\D/g, '').slice(0, 6)
                setCode(v)
                if (v.length === 6) verify(v)
              }}
            />
            <ErrorText>{error}</ErrorText>
            <Button className="w-full" busy={busy} disabled={code.length !== 6}>
              Sign in
            </Button>
            <div className="flex items-center justify-between text-sm">
              <button type="button" className="inline-flex items-center gap-1 text-zinc-500" onClick={() => { setSent(null); setError('') }}>
                <BackIcon /> Change ID
              </button>
              <button type="button" className="font-medium text-indigo-600 disabled:text-zinc-400 dark:text-indigo-400" disabled={wait > 0 || busy} onClick={() => sendCode()}>
                {wait > 0 ? `Resend in ${wait}s` : 'Send a new code'}
              </button>
            </div>
          </form>
        )}
      </div>
      <p className="pb-6 text-center text-xs text-zinc-400">
        Staff? <Link to="/admin/login" className="underline">Dashboard sign-in</Link>
      </p>
    </div>
  )
}
