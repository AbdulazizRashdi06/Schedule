import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Button, Confirm, ErrorText, Loading, Modal, Tag, TextField, cx } from '../components/ui'
import { PageTitle, seen, useAdmin } from './AdminLayout'
import SyncTokens from './SyncTokens'
import DeleteAllData from './DeleteAllData'

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

const signedIn = (iso: string | null) => {
  if (!iso) return 'Never signed in'
  const s = seen(iso)
  return s.startsWith('Today') ? 'Signed in today' : s === 'Yesterday' ? 'Signed in yesterday' : `Last signed in ${s}`
}

export default function Accounts() {
  const { isOwner, reloadSemesters } = useAdmin()
  const [data, setData] = useState<Data | null>(null)
  const [error, setError] = useState('')
  const [adding, setAdding] = useState(false)
  /** Temporary passwords shown inline, by username. */
  const [temps, setTemps] = useState<Record<string, string>>({})
  const [resetting, setResetting] = useState<number | null>(null)
  const [removing, setRemoving] = useState<Account | null>(null)
  const load = () => api.get<Data>('/admin/accounts').then(setData, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
  }, [])

  return (
    <>
      <PageTitle title="Accounts" action={<Button onClick={() => setAdding(true)}>Add account</Button>} />
      <ErrorText>{error}</ErrorText>
      {!data ? (
        <Loading />
      ) : (
        <>
          <div className="flex flex-col border-t-[2.5px] border-navy">
            {data.accounts.length === 0 && <p className="px-2 py-4 text-[15px] font-semibold text-muted">No username accounts yet.</p>}
            {data.accounts.map((a) => (
              <div key={a.id} className="tint flex flex-col gap-2.5 border-b-[1.5px] border-navy/10 px-2 py-4">
                <div className="grid grid-cols-[minmax(0,1.4fr)_110px_minmax(0,1fr)_auto] items-center gap-4 max-md:grid-cols-[minmax(0,1fr)_auto]">
                  <span className="font-mono text-[16px] font-bold [overflow-wrap:anywhere]">{a.username}</span>
                  <Tag tone={a.role === 'owner' ? 'navy' : 'ice'} className="justify-self-start px-2.5 py-[5px] text-[11px]">
                    {a.role === 'owner' ? 'Owner' : 'RSA'}
                  </Tag>
                  <span className="text-[14px] font-semibold text-muted">
                    {signedIn(a.lastSeenAt)}
                    {a.mustChangePassword && ', temporary password'}
                  </span>
                  {a.isMe ? (
                    <span className="label px-4 text-[11px]">You</span>
                  ) : a.canManage ? (
                    <span className="flex justify-end gap-1.5">
                      <Button
                        variant="secondary"
                        size="sm"
                        busy={resetting === a.id}
                        aria-label={`Reset password for ${a.username}`}
                        onClick={async () => {
                          setResetting(a.id)
                          setError('')
                          try {
                            const r = await api.post<{ username: string; temporaryPassword: string }>(`/admin/accounts/${a.id}/reset-password`)
                            setTemps({ ...temps, [a.username]: r.temporaryPassword })
                            load()
                          } catch (e) {
                            setError(errorText(e))
                          } finally {
                            setResetting(null)
                          }
                        }}
                      >
                        Reset password
                      </Button>
                      <Button variant="danger" size="sm" aria-label={`Remove ${a.username}`} onClick={() => setRemoving(a)}>
                        Remove
                      </Button>
                    </span>
                  ) : (
                    <span />
                  )}
                </div>
                {temps[a.username] && <TempPassword username={a.username} password={temps[a.username]} />}
              </div>
            ))}
          </div>
          {isOwner && (
            <section className="flex flex-col gap-2">
              <h2 className="text-[24px] font-extrabold">Owners who sign in with their student ID</h2>
              <p className="text-[15px] font-semibold text-muted">Make a student an owner from their page under Students.</p>
              <div className="flex flex-col border-t-[2.5px] border-navy">
                {data.studentOwners.length === 0 && <p className="px-2 py-4 text-[15px] font-semibold text-muted">None yet.</p>}
                {data.studentOwners.map((s) => (
                  <Link key={s.id} to={`/admin/students/${s.id}`} className="tint grid grid-cols-[minmax(0,1.4fr)_110px_minmax(0,1fr)] items-center gap-4 border-b-[1.5px] border-navy/10 px-2 py-4">
                    <span className="font-mono text-[16px] font-bold">{s.universityId}</span>
                    <Tag tone="navy" className="justify-self-start px-2.5 py-[5px] text-[11px]">
                      Owner
                    </Tag>
                    <span className="text-[14px] font-semibold text-muted">{signedIn(s.lastSeenAt)}</span>
                  </Link>
                ))}
              </div>
            </section>
          )}
        </>
      )}

      {isOwner && <SyncTokens />}

      {isOwner && (
        <DeleteAllData
          onDone={async () => {
            await reloadSemesters()
            load()
          }}
        />
      )}

      <AddAccount
        open={adding}
        isOwner={isOwner}
        onClose={() => setAdding(false)}
        onCreated={(r) => {
          setAdding(false)
          setTemps({ ...temps, [r.username]: r.temporaryPassword })
          load()
        }}
      />
      <Confirm
        open={!!removing}
        onClose={() => setRemoving(null)}
        title={`Remove ${removing?.username}?`}
        body="They’ll be signed out and can no longer use the dashboard."
        confirmLabel="Remove"
        danger
        onConfirm={async () => {
          if (removing) await api.del(`/admin/accounts/${removing.id}`)
          load()
        }}
      />
    </>
  )
}

function TempPassword({ username, password }: { username: string; password: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div role="status" className="open flex flex-wrap items-center gap-3 rounded-2xl bg-ice px-3.5 py-3">
      <span className="text-[14px] font-bold">Temporary password</span>
      <span className="font-mono text-[18px] font-bold tracking-[0.04em] select-all">{password}</span>
      <span className="text-[14px] font-semibold">Share it in person. They pick a new one at sign-in. It’s only shown once.</span>
      <button
        type="button"
        className="label ml-auto text-[11px] link-hover"
        onClick={() => {
          navigator.clipboard?.writeText(`${username} / ${password}`).then(() => setCopied(true), () => {})
        }}
      >
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>
  )
}

function AddAccount({ open, isOwner, onClose, onCreated }: { open: boolean; isOwner: boolean; onClose: () => void; onCreated: (r: { username: string; temporaryPassword: string }) => void }) {
  const [username, setUsername] = useState('')
  const [role, setRole] = useState<'rsa' | 'owner'>('rsa')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const valid = /^[a-z0-9][a-z0-9._-]{2,31}$/.test(username.trim())
  const roles = [
    { id: 'rsa' as const, label: 'RSA', hint: 'Uploads, courses, majors. No student data.' },
    { id: 'owner' as const, label: 'Owner', hint: isOwner ? 'Everything, plus students and accounts' : 'Only owners can add owners' },
  ]
  return (
    <Modal open={open} onClose={onClose} label="Add account">
      <form
        className="flex flex-col gap-[18px]"
        onSubmit={async (e) => {
          e.preventDefault()
          if (!valid || busy) return
          setBusy(true)
          setError('')
          try {
            onCreated(await api.post('/admin/accounts', { username, role }))
            setUsername('')
            setRole('rsa')
          } catch (err) {
            setError(errorText(err))
          } finally {
            setBusy(false)
          }
        }}
      >
        <h2 className="text-[32px] font-extrabold tracking-[-0.03em]">Add account</h2>
        <TextField
          label="Username"
          autoFocus
          autoCapitalize="none"
          mono
          placeholder="rsa.examinations"
          value={username}
          onChange={(e) => setUsername(e.target.value.toLowerCase())}
          boxClassName="h-[52px]"
        />
        <div className="flex flex-col gap-1.5">
          <span id="role-label" className="label text-[11px] tracking-[0.12em]">
            Role
          </span>
          <div role="radiogroup" aria-labelledby="role-label" className="grid grid-cols-2 gap-2">
            {roles.map((r) => {
              const on = r.id === role
              const off = r.id === 'owner' && !isOwner
              return (
                <button
                  key={r.id}
                  type="button"
                  role="radio"
                  aria-checked={on}
                  aria-disabled={off}
                  onClick={() => !off && setRole(r.id)}
                  className={cx(
                    'press flex min-h-[76px] flex-col items-start gap-1 rounded-2xl px-3.5 py-3 text-left',
                    on ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]',
                    off && 'opacity-55',
                  )}
                >
                  <span className="text-[16px] font-extrabold">{r.label}</span>
                  <span className="text-[13px] font-semibold">{r.hint}</span>
                </button>
              )
            })}
          </div>
        </div>
        <ErrorText>{error}</ErrorText>
        <div className="flex justify-end gap-2.5">
          <Button type="button" variant="secondary" className="px-[18px] text-[15px]" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" className="text-[15px]" disabled={!valid} busy={busy}>
            {busy ? 'Creating…' : 'Create account'}
          </Button>
        </div>
      </form>
    </Modal>
  )
}
