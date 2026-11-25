import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import type { TeachMe } from '../lib/types'
import { Icon, cx, useShake } from '../components/ui'
import { CodeStep, Decor, Screen, deviceEndpoint, waitSeconds, type CodeSent } from '../student/Login'

const delay = (ms: number): CSSProperties => ({ animationDelay: `${ms}ms` })

/** The server's own rule (TeacherEmails.Normalize): a plain address at exactly @gutech.edu.om. */
const STAFF_EMAIL = /^[a-z0-9]+(?:[._-][a-z0-9]+)*@gutech\.edu\.om$/
const clean = (raw: string) => raw.trim().toLowerCase()
const validEmail = (email: string) => email.length <= 100 && STAFF_EMAIL.test(email)

/**
 * The teacher sign-in: a GUtech staff email, then the emailed code. The code is never shown on screen, in any mode.
 * `other` is set when somebody else is signed in on this browser: they sign out first, so their device is released.
 */
export default function TeachLogin({ other }: { other?: string }) {
  const { refresh } = useSession()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState<CodeSent | null>(null)
  // What the server said about the account when the code was right: whether a name still has to be picked.
  const status = useRef<TeachMe['status']>('none')

  if (other) return <SignedInAs name={other} />

  const address = clean(email)
  const ask = async (): Promise<CodeSent> => ({ ...(await api.post<Omit<CodeSent, 'testCode'>>('/auth/teacher/request-code', { email: address })), testCode: null })

  return sent ? (
    <CodeStep
      sent={sent}
      backLabel="Back to email"
      welcome={false}
      verify={async (code) => {
        const r = await api.post<{ isNew: boolean; status: TeachMe['status'] }>('/auth/teacher/verify', { email: address, code, endpoint: await deviceEndpoint() })
        status.current = r.status
        return r
      }}
      resend={ask}
      onBack={() => setSent(null)}
      onResent={setSent}
      onSignedIn={async () => {
        await refresh()
        navigate(status.current === 'pick-name' ? '/teach/name' : '/teach', { replace: true })
      }}
    />
  ) : (
    <EmailStep email={email} setEmail={setEmail} ask={ask} onSent={setSent} />
  )
}

function EmailStep({ email, setEmail, ask, onSent }: { email: string; setEmail: (v: string) => void; ask: () => Promise<CodeSent>; onSent: (s: CodeSent) => void }) {
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [wait, setWait] = useState(0)
  const { shakeClass, shake } = useShake()
  const address = clean(email)
  const valid = validEmail(address)

  useEffect(() => {
    if (wait <= 0) return
    const t = setTimeout(() => setWait((w) => w - 1), 1000)
    return () => clearTimeout(t)
  }, [wait])

  async function send(e: React.FormEvent) {
    e.preventDefault()
    if (wait > 0 || busy) return
    if (!valid) {
      setError(
        address.endsWith('@student.gutech.edu.om')
          ? 'That’s a student address. Students sign in with their university ID.'
          : 'Enter your GUtech staff email, like firstname.lastname@gutech.edu.om.',
      )
      shake()
      return
    }
    setBusy(true)
    setError('')
    try {
      onSent(await ask())
    } catch (err) {
      const w = waitSeconds(err)
      if (w) setWait(w)
      else {
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
        <div className="rise flex flex-col gap-2.5 px-6 pt-[26px]" style={delay(300)}>
          <label htmlFor="staff-email" className="label text-[12px] tracking-[0.14em] text-muted">
            GUtech staff email
          </label>
          <div data-invalid={!!error || undefined} className={cx('field flex h-[72px] items-center gap-3 rounded-[22px] bg-paper px-5 shadow-[inset_0_0_0_2.5px_var(--color-navy)]', shakeClass)}>
            <input
              id="staff-email"
              autoFocus
              type="email"
              inputMode="email"
              autoComplete="username"
              autoCapitalize="none"
              spellCheck={false}
              placeholder="firstname.lastname@gutech.edu.om"
              value={email}
              maxLength={100}
              onChange={(e) => {
                setEmail(e.target.value)
                setError('')
              }}
              aria-invalid={!!error || undefined}
              aria-describedby="staff-email-msg"
              className="min-w-0 flex-1 bg-transparent text-[17px] font-bold text-navy outline-none placeholder:text-[15px] placeholder:font-semibold placeholder:text-[#9AA3C7]"
            />
            {valid && !error && (
              <span className="pop grid size-[30px] shrink-0 place-items-center rounded-full bg-navy text-sky">
                <Icon name="check" className="size-4" strokeWidth={3} />
              </span>
            )}
          </div>
          <div id="staff-email-msg" aria-live="polite" className="min-h-11">
            {waiting ? (
              <span className="msg text-[14px] font-semibold">Too many tries. Wait {wait} seconds, then try again.</span>
            ) : error ? (
              <span className="msg flex items-start gap-2 text-[14px] font-semibold text-alert">
                <Icon name="alert" className="mt-px size-[18px]" />
                {error}
              </span>
            ) : (
              <span className="text-[14px] font-semibold text-muted">We’ll email you a code. If it doesn’t arrive, check your junk folder.</span>
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
            {waiting ? `Try again in ${Math.floor(wait / 60)}:${String(wait % 60).padStart(2, '0')}` : busy ? 'Sending…' : 'Send me a code'}
            {!waiting && !busy && <Icon name="arrow" strokeWidth={2.6} />}
          </button>
          <p className="label text-center text-[11px] text-muted">
            Student?{' '}
            <Link to="/login" className="text-navy underline underline-offset-2">
              Sign in here
            </Link>
          </p>
        </div>
      </form>
    </Screen>
  )
}

/** Somebody else is signed in on this browser (a student without teaching rights, or a dashboard account). */
function SignedInAs({ name }: { name: string }) {
  const { logout } = useSession()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <Screen>
      <Decor />
      <div className="flex flex-1 flex-col">
        <div className="rise flex flex-col gap-2.5 px-6 pt-[26px]" style={delay(300)}>
          <span className="label text-[12px] tracking-[0.14em] text-muted">Teacher sign-in</span>
          <p className="text-[20px] leading-[1.25] font-extrabold">
            You’re signed in as <span className="font-mono break-all">{name}</span>.
          </p>
          <p className="text-[15px] font-semibold text-muted">Sign out to sign in as a teacher.</p>
          <div aria-live="polite" className="min-h-6">
            {error && (
              <span className="msg flex items-start gap-2 text-[14px] font-semibold text-alert">
                <Icon name="alert" className="mt-px size-[18px]" />
                {error}
              </span>
            )}
          </div>
        </div>
        <div className="rise mt-auto flex flex-col gap-3.5 px-6 pt-6" style={delay(360)}>
          <button
            type="button"
            aria-disabled={busy}
            onClick={async () => {
              if (busy) return
              setBusy(true)
              setError('')
              try {
                // Through the ordinary sign-out, so this person's notifications stop on this device first.
                await logout()
              } catch (e) {
                setError(errorText(e))
                setBusy(false)
              }
            }}
            className="press flex h-[60px] w-full items-center justify-center rounded-full bg-navy text-[18px] font-extrabold text-cream"
          >
            {busy ? 'Signing out…' : 'Sign out'}
          </button>
          <p className="label text-center text-[11px] text-muted">
            <Link to="/" className="text-navy underline underline-offset-2">
              Back to Classy
            </Link>
          </p>
        </div>
      </div>
    </Screen>
  )
}
