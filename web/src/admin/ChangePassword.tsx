import { useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Button, ErrorText, Field, Input, Loading, Notice } from '../components/ui'

export default function ChangePassword() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [again, setAgain] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  if (!session) return <Loading />
  if (session.kind !== 'admin') return <Navigate to="/admin/login" replace />
  const forced = session.mustChangePassword

  return (
    <div className="mx-auto flex min-h-dvh max-w-sm flex-col justify-center px-5 pb-24">
      <form
        className="space-y-4"
        onSubmit={async (e) => {
          e.preventDefault()
          if (next !== again) return setError("The new passwords don't match.")
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
        <h1 className="text-2xl font-semibold tracking-tight">Choose a new password</h1>
        {forced && <Notice>You signed in with a temporary password. Pick your own to continue.</Notice>}
        <Field label={forced ? 'Temporary password' : 'Current password'}>
          <Input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
        </Field>
        <Field label="New password" hint="At least 10 characters.">
          <Input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
        </Field>
        <Field label="New password again">
          <Input type="password" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} />
        </Field>
        <ErrorText>{error}</ErrorText>
        <Button className="w-full" busy={busy} disabled={!current || next.length < 10 || !again}>
          Save password
        </Button>
        {!forced && (
          <Button type="button" variant="ghost" className="w-full" onClick={() => navigate('/admin')}>
            Cancel
          </Button>
        )}
      </form>
    </div>
  )
}
