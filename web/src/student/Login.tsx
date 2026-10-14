import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { Link, Navigate, useNavigate } from 'react-router-dom'
import { ApiError, api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Icon, Spinner, cx, useShake, useToast } from '../components/ui'
import { BackButton } from './timetable'

interface CodeSent {
  email: string
  resendAfterSeconds: number
  testCode: string | null
}

/**
 * "24" → "24-", "240161" → "24-0161": the dash appears as soon as the first two digits are in,
 * so nobody has to type it. Deleting right after the dash removes the dash and the digit before it.
 */
const formatId = (raw: string, prev: string) => {
  let v = raw.replace(/\D/g, '').slice(0, 6)
  if (raw.length < prev.length && prev.endsWith('-') && v.length === 2) v = v.slice(0, 1)
  return v.length >= 2 ? `${v.slice(0, 2)}-${v.slice(2)}` : v
}
const validId = (id: string) => /^\d{2}-\d{4}$/.test(id)

/** "Wait 34 seconds…" from the server → 34. */
const waitSeconds = (e: unknown) => {
  if (!(e instanceof ApiError) || e.status !== 429) return 0
  const m = /(\d+)\s*(second|minute)/.exec(e.message)
  return m ? Number(m[1]) * (m[2] === 'minute' ? 60 : 1) : 60
}

export default function Login() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const [id, setId] = useState('')
  const [sent, setSent] = useState<CodeSent | null>(null)
  // Set while "You're in" plays, so the signed-in redirect doesn't cut it short.
  const [entering, setEntering] = useState(false)

  if (!entering && session?.kind === 'student') return <Navigate to="/" replace />
  if (session?.kind === 'admin') return <Navigate to="/admin" replace />

  return sent ? (
    <CodeStep
      id={id}
      sent={sent}
      onBack={() => setSent(null)}
      onResent={setSent}
      onSignedIn={async (isNew) => {
        setEntering(true)
        if (!isNew) {
          await refresh()
          navigate('/', { replace: true })
        }
      }}
      onContinue={async () => {
        await refresh()
        navigate('/', { replace: true })
      }}
    />
  ) : (
    <IdStep id={id} setId={setId} onSent={setSent} />
  )
}

function Screen({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-dvh overflow-x-hidden bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[env(safe-area-inset-top)] pb-[max(1.75rem,env(safe-area-inset-bottom))]">{children}</div>
    </div>
  )
}

const delay = (ms: number): CSSProperties => ({ animationDelay: `${ms}ms` })

/** The coloured blocks above "Ahlan.": a timetable falling into place. */
function Decor() {
  const cols: { pt: number; blocks: [string, number][] }[] = [
    { pt: 60, blocks: [['bg-navy', 1], ['bg-ice', 6]] },
    { pt: 0, blocks: [['bg-ice', 3], ['dashed', 8], ['bg-sky', 5]] },
    { pt: 104, blocks: [['bg-sky', 2]] },
    { pt: 30, blocks: [['bg-navy', 7], ['bg-ice', 4]] },
    { pt: 140, blocks: [['bg-navy', 9]] },
  ]
  return (
    <div aria-hidden="true" className="mx-6 mt-10 grid h-[250px] grid-cols-5 gap-2">
      {cols.map((c, i) => (
        <div key={i} className="flex flex-col gap-2" style={{ paddingTop: c.pt }}>
          {c.blocks.map(([bg, d], j) => (
            <div key={j} className={cx('drop h-16 rounded-[14px]', bg === 'dashed' ? 'border-2 border-dashed border-navy/25' : bg)} style={delay(d * 60)} />
          ))}
        </div>
      ))}
    </div>
  )
}

function IdStep({ id, setId, onSent }: { id: string; setId: (v: string) => void; onSent: (s: CodeSent) => void }) {
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [wait, setWait] = useState(0)
  const [waitFrom, setWaitFrom] = useState(0)
  const { shakeClass, shake } = useShake()
  const valid = validId(id)

  useEffect(() => {
    if (wait <= 0) return
    const t = setTimeout(() => setWait((w) => w - 1), 1000)
    return () => clearTimeout(t)
  }, [wait])

  async function send(e: React.FormEvent) {
    e.preventDefault()
    if (wait > 0 || busy) return
    if (!valid) {
      setError('IDs look like 24-0161: two digits, a dash, then four.')
      shake()
      return
    }
    setBusy(true)
    setError('')
    try {
      onSent(await api.post<CodeSent>('/auth/request-code', { universityId: id }))
    } catch (err) {
      const w = waitSeconds(err)
      if (w) {
        setWait(w)
        setWaitFrom(w)
      } else {
        setError(errorText(err))
        shake()
      }
      setBusy(false)
    }
  }

  const waiting = wait > 0
  return (
    <Screen>
      <Decor />
      <form onSubmit={send} noValidate className="flex flex-1 flex-col">
        <h1 className="display rise px-6 pt-7 text-[84px] leading-[0.82] tracking-[-0.06em]" style={delay(180)}>
          Ahlan.
        </h1>
        <div className="rise flex flex-col gap-2.5 px-6 pt-[26px]" style={delay(300)}>
          <label htmlFor="uid" className="label text-[12px] tracking-[0.14em] text-muted">
            University ID
          </label>
          <div data-invalid={!!error || undefined} className={cx('field flex h-[72px] items-center gap-3 rounded-[22px] bg-paper px-5 shadow-[inset_0_0_0_2.5px_var(--color-navy)]', shakeClass)}>
            <input
              id="uid"
              autoFocus
              inputMode="numeric"
              autoComplete="username"
              placeholder="24-0161"
              value={id}
              onChange={(e) => {
                setId(formatId(e.target.value, id))
                setError('')
              }}
              aria-invalid={!!error || undefined}
              aria-describedby="uid-msg"
              className="min-w-0 flex-1 bg-transparent font-mono text-[30px] font-bold tracking-[0.06em] text-navy outline-none placeholder:text-[#9AA3C7]"
            />
            {valid && !error && (
              <span className="pop grid size-[30px] place-items-center rounded-full bg-navy text-sky">
                <Icon name="check" className="size-4" strokeWidth={3} />
              </span>
            )}
          </div>
          <div id="uid-msg" aria-live="polite" className="min-h-11">
            {waiting ? (
              <span className="msg flex items-center gap-2.5 text-[14px] font-semibold">
                <svg aria-hidden="true" viewBox="0 0 36 36" className="size-7 shrink-0 -rotate-90">
                  <circle cx="18" cy="18" r="15" fill="none" stroke="#E3DCCD" strokeWidth="4" />
                  <circle
                    cx="18"
                    cy="18"
                    r="15"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="4"
                    strokeLinecap="round"
                    strokeDasharray="94.25"
                    style={{ strokeDashoffset: 94.25 * (1 - wait / waitFrom), transition: 'stroke-dashoffset 1000ms linear' }}
                  />
                </svg>
                Too many tries. Wait {wait} seconds, then try again.
              </span>
            ) : error ? (
              <span className="msg flex items-start gap-2 text-[14px] font-semibold text-alert">
                <Icon name="alert" className="mt-px size-[18px]" />
                {error}
              </span>
            ) : (
              <span className="text-[14px] font-semibold text-muted">We’ll email you a code.</span>
            )}
          </div>
        </div>
        <div className="rise mt-auto flex flex-col gap-3.5 px-6 pt-6" style={delay(360)}>
          <button
            type="submit"
            aria-disabled={waiting || busy}
            className={cx(
              'press flex h-[60px] w-full items-center justify-center gap-2.5 rounded-full text-[18px] font-extrabold',
              valid && !waiting ? 'bg-navy text-cream' : 'bg-navy/10 text-navy/60',
            )}
          >
            {waiting ? `Try again in 0:${String(wait).padStart(2, '0')}` : busy ? 'Sending…' : 'Send me a code'}
            {!waiting && !busy && <Icon name="arrow" strokeWidth={2.6} />}
          </button>
          <p className="label text-center text-[11px] text-muted">
            Staff?{' '}
            <Link to="/admin/login" className="text-navy underline underline-offset-2">
              Sign in here
            </Link>
          </p>
        </div>
      </form>
    </Screen>
  )
}

type Phase = 'entry' | 'checking' | 'wrong' | 'expired' | 'ok' | 'done'

function CodeStep({
  id,
  sent,
  onBack,
  onResent,
  onSignedIn,
  onContinue,
}: {
  id: string
  sent: CodeSent
  onBack: () => void
  onResent: (s: CodeSent) => void
  onSignedIn: (isNew: boolean) => void
  onContinue: () => void
}) {
  const [code, setCode] = useState('')
  const [phase, setPhase] = useState<Phase>('entry')
  const [message, setMessage] = useState('')
  const [resendIn, setResendIn] = useState(sent.resendAfterSeconds)
  const [resendError, setResendError] = useState('')
  const [continuing, setContinuing] = useState(false)
  const { shakeClass, shake } = useShake()
  const toast = useToast()
  const input = useRef<HTMLInputElement>(null)
  const timers = useRef<ReturnType<typeof setTimeout>[]>([])

  useEffect(() => {
    if (resendIn <= 0 || phase === 'ok' || phase === 'done') return
    const t = setTimeout(() => setResendIn((w) => w - 1), 1000)
    return () => clearTimeout(t)
  }, [resendIn, phase])
  useEffect(() => () => timers.current.forEach(clearTimeout), [])

  async function verify(value: string) {
    setPhase('checking')
    try {
      const r = await api.post<{ isNew: boolean }>('/auth/verify', { universityId: id, code: value })
      setPhase('ok')
      timers.current.push(
        setTimeout(() => {
          if (r.isNew) setPhase('done')
          onSignedIn(r.isNew)
        }, 750),
      )
    } catch (e) {
      const text = errorText(e)
      const left = /(\d+) tr(y|ies) left/.exec(text)
      shake()
      if (left) {
        setPhase('wrong')
        setMessage(`That code isn’t right. ${left[1]} ${left[1] === '1' ? 'try' : 'tries'} left.`)
        timers.current.push(
          setTimeout(() => {
            setCode('')
            input.current?.focus()
          }, 650),
        )
      } else if (/expired|new code|new one/i.test(text)) {
        setPhase('expired')
        setMessage(/expired/i.test(text) ? 'This code expired. They last 10 minutes, so grab a fresh one.' : text)
        timers.current.push(setTimeout(() => input.current?.focus(), 50))
      } else {
        setPhase('wrong')
        setMessage(text)
        timers.current.push(
          setTimeout(() => {
            setCode('')
            input.current?.focus()
          }, 650),
        )
      }
    }
  }

  async function resend() {
    if (resendIn > 0 && phase !== 'expired') return
    setResendError('')
    try {
      const r = await api.post<CodeSent>('/auth/request-code', { universityId: id })
      onResent(r)
      setResendIn(r.resendAfterSeconds)
      setCode('')
      setPhase('entry')
      setMessage('')
      toast.show('New code sent. The old one won’t work.')
      input.current?.focus()
    } catch (e) {
      const w = waitSeconds(e)
      if (w) setResendIn(w)
      setResendError(errorText(e))
    }
  }

  if (phase === 'done')
    return (
      <Screen>
        <div className="flex flex-1 flex-col justify-center gap-[18px] px-6 pt-10">
          <div className="badge-in grid size-24 place-items-center rounded-[32px] bg-navy text-sky">
            <svg aria-hidden="true" viewBox="0 0 24 24" className="size-12" fill="none" stroke="currentColor" strokeWidth={3} strokeLinecap="round" strokeLinejoin="round">
              <path className="draw" d="M20 6L9 17l-5-5" />
            </svg>
          </div>
          <h1 className="display rise text-[72px] leading-[0.86] tracking-[-0.06em]" style={delay(60)}>
            You’re in.
          </h1>
          <p className="rise text-[18px] leading-[1.35] font-semibold text-muted" style={delay(120)}>
            Takes about a minute to set up your semester. Then your week is ready.
          </p>
        </div>
        <div className="rise px-6 pt-6" style={delay(240)}>
          <button
            type="button"
            autoFocus
            aria-disabled={continuing}
            onClick={() => {
              if (continuing) return
              setContinuing(true)
              onContinue()
            }}
            className="press flex h-[60px] w-full items-center justify-center gap-2.5 rounded-full bg-navy text-[18px] font-extrabold text-cream"
          >
            Let’s set you up <Icon name="arrow" strokeWidth={2.6} />
          </button>
        </div>
      </Screen>
    )

  const busy = phase === 'checking' || phase === 'ok'
  const canResend = resendIn <= 0
  return (
    <Screen>
      <header className="flex items-center justify-between px-6 pt-7">
        <BackButton label="Back to university ID" onClick={onBack} />
        <span className="label text-[12px] tracking-[0.14em] text-muted">Step 2 of 2</span>
      </header>
      <div className="flex flex-col gap-3.5 px-6 pt-10">
        <h1 className="display rise text-[56px] leading-[0.88] tracking-[-0.05em]" style={delay(60)}>
          Check your inbox.
        </h1>
        <p className="rise text-[17px] leading-[1.35] font-semibold" style={delay(120)}>
          We sent a 6-digit code to <span className="rounded-lg bg-ice px-2 py-0.5 font-mono text-[14px] break-all">{sent.email}</span>
        </p>
        {sent.testCode && (
          <p className="rise rounded-[14px] bg-ice px-3.5 py-2.5 text-[14px] font-bold" style={delay(150)}>
            Test mode: your code is <span className="font-mono tracking-[0.2em]">{sent.testCode}</span>
          </p>
        )}
      </div>
      <div className="rise flex flex-col gap-3.5 px-6 pt-[30px]" style={delay(180)}>
        <div className={cx('relative', shakeClass)}>
          <label htmlFor="otp" className="sr">
            6-digit code
          </label>
          <input
            ref={input}
            id="otp"
            autoFocus
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            value={code}
            disabled={busy}
            aria-invalid={phase === 'wrong' || phase === 'expired' || undefined}
            aria-describedby="otp-msg"
            onChange={(e) => {
              const v = e.target.value.replace(/\D/g, '').slice(0, 6)
              setCode(v)
              if (phase === 'wrong' && v) setPhase('entry')
              if (v.length === 6 && phase !== 'expired') verify(v)
            }}
            className="absolute inset-0 z-10 size-full cursor-text bg-transparent text-[16px] text-transparent caret-transparent opacity-[0.01] outline-none"
          />
          <div aria-hidden="true" className="grid grid-cols-6 gap-2">
            {Array.from({ length: 6 }, (_, i) => {
              const ch = code.charAt(i)
              const active = i === code.length && (phase === 'entry' || phase === 'wrong')
              const success = phase === 'ok'
              return (
                <span
                  key={i}
                  className={cx(
                    'flex h-16 items-center justify-center rounded-2xl border-[2.5px] font-mono text-[28px] font-bold transition-[background-color,border-color,color,transform] duration-[260ms] ease-out',
                    success
                      ? '-translate-y-1 border-navy bg-navy text-cream'
                      : (phase === 'wrong' && ch) || phase === 'expired'
                        ? 'border-alert bg-paper'
                        : phase === 'checking' || active
                          ? 'border-navy bg-paper'
                          : ch
                            ? 'border-navy/50 bg-paper'
                            : 'border-navy/[0.18] bg-paper',
                    active && 'shadow-[0_0_0_4px_var(--color-ice)]',
                  )}
                  style={success ? { transitionDelay: `${i * 45}ms` } : undefined}
                >
                  {ch && <span className="pop">{ch}</span>}
                  {active && <span className="caret" />}
                </span>
              )
            })}
          </div>
        </div>
        <div id="otp-msg" aria-live="polite" className="min-h-12">
          {busy ? (
            <span className="msg flex items-center gap-2.5 text-[15px] font-semibold text-muted">
              <Spinner /> Checking…
            </span>
          ) : phase === 'wrong' || phase === 'expired' ? (
            <span className="msg flex items-start gap-2 text-[15px] font-semibold text-alert">
              <Icon name={phase === 'expired' ? 'clock' : 'alert'} className="mt-px size-[18px]" />
              {message}
            </span>
          ) : (
            <span className="text-[14px] font-semibold text-muted">Can’t see it? Check junk, or wait a minute.</span>
          )}
        </div>
      </div>
      <div className="relative mt-auto flex flex-col gap-3 px-6 pt-6">
        <div role="status" aria-live="polite" data-shown={toast.shown} className="toast absolute inset-x-6 bottom-full mb-3 flex items-center gap-2.5 rounded-[18px] bg-ice px-4 py-3 text-[15px] font-bold">
          <Icon name="check" className="size-[18px]" strokeWidth={2.6} />
          {toast.text}
        </div>
        {resendError && <p className="text-center text-[14px] font-semibold text-alert">{resendError}</p>}
        {phase === 'expired' ? (
          <button type="button" onClick={resend} className="press h-[60px] rounded-full bg-navy text-[18px] font-extrabold text-cream">
            Send a new code
          </button>
        ) : (
          <button
            type="button"
            onClick={resend}
            aria-disabled={!canResend}
            className={cx('press label h-14 rounded-full border-[2.5px] text-[13px]', canResend ? 'border-navy text-navy' : 'border-navy/[0.18] text-navy/55')}
          >
            {canResend ? 'Resend code' : `Resend code in ${Math.floor(resendIn / 60)}:${String(resendIn % 60).padStart(2, '0')}`}
          </button>
        )}
      </div>
    </Screen>
  )
}
