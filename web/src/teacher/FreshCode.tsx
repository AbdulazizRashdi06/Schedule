import { useCallback, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ApiError, api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Button, ErrorText } from '../components/ui'
import { CodeStep, Screen, deviceEndpoint, type CodeSent } from '../student/Login'
import { BackButton } from '../student/timetable'

/** Thrown by `run` when the person backed out of the code step: nothing was done, and there is nothing to report. */
class Cancelled extends Error {}

/** "raed.bolbol@gutech.edu.om" → "r…@gutech.edu.om" */
const masked = (email: string) => email.replace(/^(.).*(@.*)$/, '$1…$2')

/**
 * The fresh-code rule: changing a class tells every student in it, so the browser must have had an emailed sign-in
 * code entered in the last week (and, for a student with teaching rights, since they were given them). `run(fn)` asks
 * for a new code first when the session says it is due, then calls `fn`; and when the server refuses `fn` for that
 * reason, asks and calls it once more. Render `step` somewhere in the page: it is the full-screen code step.
 */
export function useFreshCode() {
  const { session, refresh } = useSession()
  const [asking, setAsking] = useState(false)
  const waiting = useRef<{ done: () => void; back: () => void } | null>(null)
  // A dashboard password account has no emailed code and is exempt.
  const due = !!session && session.kind !== 'admin' && session.codeFresh === false

  /** Opens the step and waits until a new code was entered (or throws Cancelled). */
  const ask = useCallback(
    () =>
      new Promise<void>((done, fail) => {
        waiting.current = { done, back: () => fail(new Cancelled()) }
        setAsking(true)
      }),
    [],
  )

  const run = useCallback(
    async <T,>(fn: () => Promise<T>): Promise<T> => {
      if (due) await ask()
      try {
        return await fn()
      } catch (e) {
        if (!(e instanceof ApiError && e.status === 403 && (e.data as { needsCode?: boolean } | undefined)?.needsCode)) throw e
        await ask()
        return await fn()
      }
    },
    [due, ask],
  )

  const close = (ok: boolean) => {
    const w = waiting.current
    waiting.current = null
    setAsking(false)
    if (ok) w?.done()
    else w?.back()
  }

  const step: ReactNode =
    asking && session?.name
      ? createPortal(
          <div role="dialog" aria-modal="true" aria-label="Confirm it’s you" className="fixed inset-0 z-[70] overflow-y-auto bg-cream">
            <FreshStep
              kind={session.kind === 'teacher' ? 'teacher' : 'student'}
              name={session.name}
              onBack={() => close(false)}
              onDone={async () => {
                await refresh()
                close(true)
              }}
            />
          </div>,
          document.body,
        )
      : null

  return { run, step, cancelled: (e: unknown) => e instanceof Cancelled }
}

/** Says why a new code is needed and sends it, then the same code screen as signing in. */
function FreshStep({ kind, name, onBack, onDone }: { kind: 'teacher' | 'student'; name: string; onBack: () => void; onDone: () => void }) {
  const [sent, setSent] = useState<CodeSent | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  // The sign-in endpoints of whoever is signed in: a teacher's email, or a student's university ID.
  const request = async (): Promise<CodeSent> =>
    kind === 'teacher'
      ? { ...(await api.post<Omit<CodeSent, 'testCode'>>('/auth/teacher/request-code', { email: name })), testCode: null }
      : // Never on screen here either: this code lets a class be changed.
        { ...(await api.post<CodeSent>('/auth/request-code', { universityId: name })), testCode: null }
  const verify = async (code: string) =>
    kind === 'teacher'
      ? api.post<{ isNew: boolean }>('/auth/teacher/verify', { email: name, code, endpoint: await deviceEndpoint() })
      : api.post<{ isNew: boolean }>('/auth/verify', { universityId: name, code, endpoint: await deviceEndpoint() })

  if (sent) return <CodeStep sent={sent} backLabel="Back" welcome={false} verify={verify} resend={request} onBack={onBack} onResent={setSent} onSignedIn={onDone} />

  return (
    <Screen>
      <header className="flex items-center justify-between px-6 pt-7">
        <BackButton label="Back" onClick={onBack} />
        <span className="label text-[12px] tracking-[0.14em] text-muted">Step 1 of 2</span>
      </header>
      <div className="flex flex-col gap-3.5 px-6 pt-10">
        <h1 className="display rise text-[56px] leading-[0.88] tracking-[-0.05em]">Confirm it’s you.</h1>
        <p className="rise text-[17px] leading-[1.35] font-semibold" style={{ animationDelay: '60ms' }}>
          {kind === 'teacher' ? (
            <>
              Your last sign-in code is more than a week old. We’ll email a new one to{' '}
              <span className="rounded-lg bg-ice px-2 py-0.5 font-mono text-[14px] break-all">{masked(name)}</span>
            </>
          ) : (
            'Before you change a class, we’ll email a new sign-in code to your university address.'
          )}
        </p>
        <p className="rise text-[15px] font-semibold text-muted" style={{ animationDelay: '120ms' }}>
          A change tells every student in the class, so we check once a week that it’s really you. Nothing has been changed yet.
        </p>
        <ErrorText>{error}</ErrorText>
      </div>
      <div className="rise mt-auto px-6 pt-6" style={{ animationDelay: '180ms' }}>
        <Button
          size="lg"
          className="w-full"
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              setSent(await request())
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          {busy ? 'Sending…' : 'Email me a code'}
        </Button>
      </div>
    </Screen>
  )
}
