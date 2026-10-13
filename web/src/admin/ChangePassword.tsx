import { useState } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Button, ErrorText, Icon, TextField, cx } from '../components/ui'
import { SplitPage } from './AdminLogin'

/** Change password: a live checklist, a match check, and Save stays off until everything passes. */
export default function ChangePassword() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const location = useLocation()
  const fromSignIn = (location.state as { current?: string } | null)?.current
  const [current, setCurrent] = useState(fromSignIn ?? '')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  if (!session) return <div className="min-h-dvh bg-cream" aria-busy="true" />
  if (session.kind !== 'admin') return <Navigate to="/admin/login" replace />
  const forced = session.mustChangePassword
  const username = session.name ?? ''

  // Same rules as the server (AuthEndpoints.PasswordProblem).
  const rules = [
    { text: 'At least 12 characters', ok: next.length >= 12 },
    { text: 'Has a number', ok: /\d/.test(next) },
    { text: 'Not your username', ok: next.length > 0 && !next.toLowerCase().includes(username.toLowerCase()) },
  ]
  const match = again.length > 0 && again === next
  const ready = rules.every((r) => r.ok) && match && current.length > 0

  return (
    <SplitPage kicker={forced ? 'First sign-in' : 'Your account'} title="New password." size={104} foot={`Signed in as ${username}`}>
      <form
        className="flex w-full max-w-[420px] flex-col gap-5"
        onSubmit={async (e) => {
          e.preventDefault()
          if (!ready || busy) return
          setBusy(true)
          setError('')
          try {
            await api.post('/auth/change-password', { currentPassword: current, newPassword: next })
            await refresh()
            navigate('/admin', { replace: true })
          } catch (err) {
            setError(errorText(err))
            setBusy(false)
          }
        }}
      >
        {!fromSignIn && (
          <TextField
            label={forced ? 'Temporary password' : 'Current password'}
            type="password"
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            boxClassName="h-14 rounded-2xl"
            className="[&_input]:text-[18px] [&_label]:text-[12px]"
          />
        )}
        <TextField
          label="New password"
          type="password"
          autoFocus
          autoComplete="new-password"
          value={next}
          aria-describedby="rules"
          onChange={(e) => setNext(e.target.value)}
          boxClassName="h-14 rounded-2xl"
          className="[&_input]:text-[18px] [&_label]:text-[12px]"
        />
        <ul id="rules" className="flex flex-col gap-2.5">
          {rules.map((r) => (
            <li key={r.text} className="flex items-center gap-2.5 text-[15px] font-semibold">
              <span className={cx('grid size-[22px] shrink-0 place-items-center rounded-full transition-colors duration-200', r.ok ? 'bg-navy text-cream' : 'border-2 border-navy/30')}>
                {r.ok && <Icon name="check" className="pop size-3" strokeWidth={4} />}
              </span>
              <span>
                {r.text}
                <span className="sr">{r.ok ? ', done' : ', not yet'}</span>
              </span>
            </li>
          ))}
        </ul>
        <div className="flex flex-col gap-2">
          <TextField
            label="Type it again"
            type="password"
            autoComplete="new-password"
            value={again}
            invalid={again.length >= next.length && again.length > 0 && !match}
            onChange={(e) => setAgain(e.target.value)}
            boxClassName="h-14 rounded-2xl"
            className="[&_input]:text-[18px] [&_label]:text-[12px]"
          />
          <span aria-live="polite" className="min-h-5 text-[14px] font-bold text-alert">
            {again.length >= next.length && again.length > 0 && !match ? 'These don’t match yet.' : ''}
          </span>
        </div>
        <ErrorText>{error}</ErrorText>
        <Button type="submit" className="h-14 text-[17px]" disabled={!ready} busy={busy}>
          {busy ? 'Saving…' : 'Save and continue'}
        </Button>
        {!forced && (
          <Button type="button" variant="ghost" onClick={() => navigate('/admin')}>
            Cancel
          </Button>
        )}
      </form>
    </SplitPage>
  )
}
