import { useState } from 'react'
import { Link, Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Button, ErrorText, Field, Input, ThemeToggle } from '../components/ui'

export default function AdminLogin() {
  const { session, refresh } = useSession()
  const navigate = useNavigate()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  if (session?.kind === 'admin') return <Navigate to={session.mustChangePassword ? '/admin/password' : '/admin'} replace />

  return (
    <div className="mx-auto flex min-h-dvh max-w-sm flex-col px-5">
      <div className="flex justify-end pt-2">
        <ThemeToggle />
      </div>
      <form
        className="flex flex-1 flex-col justify-center space-y-4 pb-24"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          setError('')
          try {
            const r = await api.post<{ mustChangePassword: boolean }>('/auth/admin-login', { username, password })
            await refresh()
            navigate(r.mustChangePassword ? '/admin/password' : '/admin', { replace: true })
          } catch (err) {
            setError(errorText(err))
            setBusy(false)
          }
        }}
      >
        <div className="mb-2">
          <h1 className="text-2xl font-semibold tracking-tight">Dashboard sign-in</h1>
          <p className="text-[15px] text-zinc-500">For RSA staff and admins with a username.</p>
        </div>
        <Field label="Username">
          <Input autoFocus autoComplete="username" autoCapitalize="none" value={username} onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field label="Password">
          <Input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <ErrorText>{error}</ErrorText>
        <Button busy={busy} disabled={!username || !password}>
          Sign in
        </Button>
        <p className="pt-4 text-center text-sm text-zinc-500">
          Owners and students sign in with their <Link to="/login" className="underline">university ID</Link>.
        </p>
      </form>
    </div>
  )
}
