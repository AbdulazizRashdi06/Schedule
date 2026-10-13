import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { Badge, Button, Card, Confirm, ErrorText, Field, Input, Loading, Notice, Select, Sheet } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface Account {
  id: number
  username: string
  role: 'owner' | 'rsa'
  mustChangePassword: boolean
  createdBy: string | null
  lastSeenAt: string | null
  isMe: boolean
  canManage: boolean
}
interface Data {
  accounts: Account[]
  studentOwners: { id: number; universityId: string; lastSeenAt: string | null }[]
}

export default function Accounts() {
  const { isOwner } = useAdmin()
  const { session } = useSession()
  const [data, setData] = useState<Data | null>(null)
  const [error, setError] = useState('')
  const [adding, setAdding] = useState(false)
  const [temp, setTemp] = useState<{ username: string; temporaryPassword: string } | null>(null)
  const [confirm, setConfirm] = useState<{ kind: 'reset' | 'remove'; account: Account } | null>(null)
  const load = () => api.get<Data>('/admin/accounts').then(setData, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
  }, [])

  return (
    <>
      <PageTitle
        title={isOwner ? 'Admins' : 'Accounts'}
        sub={isOwner ? 'Dashboard accounts. Owners can add owners and RSA accounts.' : 'You can add and remove RSA accounts.'}
        action={<Button onClick={() => setAdding(true)}>+ Add account</Button>}
      />
      <ErrorText>{error}</ErrorText>
      {session?.kind === 'admin' && (
        <p className="mb-4 text-sm">
          <Link to="/admin/password" className="text-indigo-600 underline dark:text-indigo-400">Change my password</Link>
        </p>
      )}
      {!data ? (
        <Loading />
      ) : (
        <div className="space-y-6">
          <Card className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {data.accounts.length === 0 && <p className="p-4 text-sm text-zinc-500">No username accounts yet.</p>}
            {data.accounts.map((a) => (
              <div key={a.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{a.username}</span>
                    <Badge tone={a.role === 'owner' ? 'indigo' : 'gray'}>{a.role === 'owner' ? 'Owner' : 'RSA'}</Badge>
                    {a.isMe && <Badge tone="green">You</Badge>}
                    {a.mustChangePassword && <Badge tone="amber">Temporary password</Badge>}
                  </div>
                  <div className="text-xs text-zinc-500">
                    {a.createdBy && `Added by ${a.createdBy}`}
                    {a.lastSeenAt ? ` · last sign-in ${new Date(a.lastSeenAt).toLocaleDateString()}` : ' · never signed in'}
                  </div>
                </div>
                {a.canManage && (
                  <div className="flex gap-1">
                    <Button variant="ghost" className="h-9 text-sm" onClick={() => setConfirm({ kind: 'reset', account: a })}>
                      Reset password
                    </Button>
                    <Button variant="ghost" className="h-9 text-sm text-red-600 dark:text-red-400" onClick={() => setConfirm({ kind: 'remove', account: a })}>
                      Remove
                    </Button>
                  </div>
                )}
              </div>
            ))}
          </Card>
          {isOwner && (
            <section>
              <h2 className="mb-1 text-sm font-semibold text-zinc-500">Owners who sign in with their student ID</h2>
              <p className="mb-2 text-sm text-zinc-500">Make a student an owner from their page under Students.</p>
              <Card className="divide-y divide-zinc-100 dark:divide-zinc-800">
                {data.studentOwners.length === 0 && <p className="p-4 text-sm text-zinc-500">None yet.</p>}
                {data.studentOwners.map((s) => (
                  <Link key={s.id} to={`/admin/students/${s.id}`} className="flex items-center justify-between px-4 py-3 hover:bg-zinc-50 dark:hover:bg-zinc-800/50">
                    <span className="font-mono">{s.universityId}</span>
                    <Badge tone="indigo">Owner</Badge>
                  </Link>
                ))}
              </Card>
            </section>
          )}
        </div>
      )}

      <AddAccount
        open={adding}
        isOwner={isOwner}
        onClose={() => setAdding(false)}
        onCreated={(r) => {
          setAdding(false)
          setTemp(r)
          load()
        }}
      />
      <Sheet open={!!temp} onClose={() => setTemp(null)} title="Temporary password">
        {temp && (
          <div className="space-y-3">
            <p className="text-sm text-zinc-600 dark:text-zinc-400">
              Give these to <b>{temp.username}</b>. They sign in at <span className="font-mono">/admin/login</span> and must choose their own password. This is the only time it's shown.
            </p>
            <div className="rounded-xl bg-zinc-100 p-3 font-mono text-lg tracking-wide select-all dark:bg-zinc-800">{temp.temporaryPassword}</div>
            <Button className="w-full" onClick={() => navigator.clipboard?.writeText(`${temp.username} / ${temp.temporaryPassword}`)}>
              Copy username and password
            </Button>
          </div>
        )}
      </Sheet>
      <Confirm
        open={!!confirm}
        onClose={() => setConfirm(null)}
        title={confirm?.kind === 'reset' ? `Reset ${confirm.account.username}'s password?` : `Remove ${confirm?.account.username}?`}
        body={confirm?.kind === 'reset' ? 'They will get a new temporary password and must change it at sign-in.' : 'They will be signed out and can no longer use the dashboard.'}
        confirmLabel={confirm?.kind === 'reset' ? 'Reset' : 'Remove'}
        danger={confirm?.kind === 'remove'}
        onConfirm={async () => {
          if (!confirm) return
          if (confirm.kind === 'reset') setTemp(await api.post(`/admin/accounts/${confirm.account.id}/reset-password`))
          else await api.del(`/admin/accounts/${confirm.account.id}`)
          load()
        }}
      />
      {!isOwner && <div className="mt-6"><Notice>Only owners can add owner accounts.</Notice></div>}
    </>
  )
}

function AddAccount({ open, isOwner, onClose, onCreated }: { open: boolean; isOwner: boolean; onClose: () => void; onCreated: (r: { username: string; temporaryPassword: string }) => void }) {
  const [username, setUsername] = useState('')
  const [role, setRole] = useState('rsa')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <Sheet open={open} onClose={onClose} title="Add a dashboard account">
      <form
        className="space-y-3"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          setError('')
          try {
            onCreated(await api.post('/admin/accounts', { username, role }))
            setUsername('')
          } catch (err) {
            setError(errorText(err))
          } finally {
            setBusy(false)
          }
        }}
      >
        <Field label="Username" hint="Letters, digits, dots or dashes.">
          <Input autoCapitalize="none" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="e.g. rsa-office" />
        </Field>
        <Field label="Role">
          <Select className="w-full" value={role} onChange={(e) => setRole(e.target.value)} disabled={!isOwner}>
            <option value="rsa">RSA: schedule, sections and majors; no student data</option>
            {isOwner && <option value="owner">Owner: everything, including students</option>}
          </Select>
        </Field>
        <ErrorText>{error}</ErrorText>
        <Button className="w-full" busy={busy} disabled={username.trim().length < 3}>
          Create account
        </Button>
      </form>
    </Sheet>
  )
}
