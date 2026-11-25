import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { viewAs } from '../lib/viewAs'
import { Button, Checkbox, Confirm, ErrorText, Loading, Modal, Notice, Select, Tag, TextField, Toast, cx, useToast } from '../components/ui'
import { PageTitle, Th, seen, shortDate, useAdmin } from './AdminLayout'

interface Waiting {
  linkId: number
  teacherId: number
  /** Null for a teaching assistant: they sign in as a student. */
  email: string | null
  /** The account's shown name. */
  teacher: string
  name: string
  sections: number
  conflict: boolean
  /** Who has the name now: a staff email, or a teaching assistant's shown name. */
  heldBy: string | null
  at: string
}
interface NameLink {
  linkId: number
  name: string
  state: 'auto' | 'approved' | 'pending' | 'rejected'
  how: 'email' | 'picked' | 'staff'
  conflict: boolean
  sections: number
  /** The university re-spelled the name: staff confirm it is the same person. */
  suggestedName: string | null
}
interface Teacher {
  id: number
  email: string | null
  name: string
  /** "student" is a student account with teaching rights (a teaching assistant). */
  kind: 'staff' | 'student'
  /** Owners only. */
  universityId: string | null
  studentId: number | null
  frozen: boolean
  frozenAt: string | null
  frozenBy: string | null
  lastSeenAt: string | null
  createdAt: string
  createdBy: string | null
  names: NameLink[]
  added: { code: string; number: string; named: string; removed?: boolean; by: string | null }[]
  hidden: number
  changesToday: number
  upcomingChanges: number
  key: string
}
interface TimetableName {
  name: string
  sections: number
  placeholder: boolean
  teacherId: number | null
  key: string
}
interface Data {
  changesEnabled: boolean
  waiting: Waiting[]
  teachers: Teacher[]
  names: TimetableName[]
  blocked: { email: string; blockedAt: string; by: string | null }[]
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`
const lastSeen = (iso: string | null) => {
  if (!iso) return 'Never signed in'
  const s = seen(iso)
  return s.startsWith('Today') ? 'Active today' : s === 'Yesterday' ? 'Active yesterday' : `Last active ${s}`
}
const STATE: Record<NameLink['state'], string> = { auto: 'Linked by email', approved: 'Approved', pending: 'Waiting', rejected: 'Rejected' }

/**
 * Teachers, for owners and RSA: approve the timetable names teachers picked, attach and detach names, freeze, sign out,
 * remove and unblock. Owners also switch all class changes off and open the teacher side as any teacher or name.
 * Nothing here is about students: a teaching assistant is a name (owners alone see whose student account it is).
 */
export default function Teachers() {
  const { isOwner, reloadTeacherBadge } = useAdmin()
  const navigate = useNavigate()
  const [data, setData] = useState<Data | null>(null)
  const [error, setError] = useState('')
  /** The action that is running, and the last one that failed, by the row they belong to. */
  const [busy, setBusy] = useState('')
  const [failed, setFailed] = useState<{ row: string; text: string } | null>(null)
  const [attach, setAttach] = useState<Record<number, string>>({})
  const [removing, setRemoving] = useState<Teacher | null>(null)
  const [signingOut, setSigningOut] = useState<Teacher | null>(null)
  const [switching, setSwitching] = useState(false)
  const [q, setQ] = useState('')
  const toast = useToast()

  const load = useCallback(
    () =>
      api.get<Data>('/admin/teachers').then(
        (d) => {
          setData(d)
          setError('')
          // The sidebar's count follows what was just approved, rejected or removed here.
          reloadTeacherBadge()
        },
        (e) => setError(errorText(e)),
      ),
    [reloadTeacherBadge],
  )
  useEffect(() => {
    void load()
  }, [load])

  /** One tap: run it, say so, and read the page again (also after a failure: "already decided" means it is stale). */
  const act = async (row: string, key: string, fn: () => Promise<unknown>, said: string) => {
    if (busy) return
    setBusy(key)
    setFailed(null)
    try {
      await fn()
      toast.show(said)
    } catch (e) {
      setFailed({ row, text: errorText(e) })
    } finally {
      await load()
      setBusy('')
    }
  }
  const view = (key: string, name: string) => {
    viewAs.set(key, name)
    navigate('/teach')
  }
  const problem = (row: string) => failed?.row === row && <ErrorText>{failed.text}</ErrorText>

  if (!data)
    return (
      <>
        <PageTitle title="Teachers" />
        <ErrorText>{error}</ErrorText>
        {!error && <Loading />}
      </>
    )

  const byId = new Map(data.teachers.map((t) => [t.id, t]))
  const label = (t: Teacher) => t.email ?? t.name
  /** Names nobody holds, for "attach a name". Placeholders ("CS1 CS1") are nobody's name. */
  const free = data.names.filter((n) => n.teacherId === null && !n.placeholder)
  const term = q.trim().toLowerCase()
  const holderOf = (n: TimetableName) => (n.teacherId !== null ? byId.get(n.teacherId) : undefined)
  const names = term ? data.names.filter((n) => `${n.name} ${holderOf(n) ? label(holderOf(n)!) : ''}`.toLowerCase().includes(term)) : data.names

  return (
    <>
      <PageTitle
        title="Teachers"
        sub={`${plural(data.teachers.length, 'teacher')} on Classy${data.waiting.length ? `, ${data.waiting.length} waiting for approval` : ''}. Their names, emails and counts only.`}
        action={
          isOwner && (
            <Button variant={data.changesEnabled ? 'secondary' : 'primary'} className={data.changesEnabled ? undefined : 'bg-alert! text-cream!'} onClick={() => setSwitching(true)}>
              Teacher class changes: {data.changesEnabled ? 'On' : 'Off'}
            </Button>
          )
        }
      />
      <ErrorText>{error}</ErrorText>
      {!data.changesEnabled && (
        <Notice tone="warn" className="max-w-[720px]">
          Teacher class changes are switched off for everyone. Changes already made stay, and undo still works. {isOwner ? 'Switch them back on with the button above.' : 'Only an owner can switch them back on.'}
        </Notice>
      )}

      {data.waiting.length > 0 && (
        <section className="flex flex-col gap-2">
          <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">Waiting for approval</h2>
          <p className="text-[15px] font-semibold text-muted">Each picked a name the email didn’t match. Approving gives them that name’s classes.</p>
          <div className="flex flex-col border-t-[2.5px] border-navy">
            {data.waiting.map((w) => {
              const row = `w-${w.linkId}`
              const others = data.waiting.filter((o) => o.linkId !== w.linkId && o.name === w.name).map((o) => o.email ?? o.teacher)
              return (
                <div key={w.linkId} className="tint flex flex-col gap-2 border-b-[1.5px] border-navy/10 px-2 py-4">
                  <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2.5">
                    <span className="flex min-w-0 flex-col gap-0.5">
                      <span className="text-[17px] font-extrabold [overflow-wrap:anywhere]">
                        {w.email ?? w.teacher} <span className="font-semibold text-muted">says they are</span> {w.name}
                      </span>
                      <span className="text-[14px] font-semibold text-muted">
                        {plural(w.sections, 'section')} this semester · asked {shortDate(w.at)}
                      </span>
                    </span>
                    <span className="flex shrink-0 gap-1.5">
                      <Button size="sm" busy={busy === `approve-${w.linkId}`} disabled={!!busy && busy !== `approve-${w.linkId}`} aria-label={`Approve ${w.email ?? w.teacher} as ${w.name}`} onClick={() => act(row, `approve-${w.linkId}`, () => api.post(`/admin/teachers/links/${w.linkId}/approve`), `Approved. ${w.name}’s classes are theirs now.`)}>
                        Approve
                      </Button>
                      <Button variant="danger" size="sm" busy={busy === `reject-${w.linkId}`} disabled={!!busy && busy !== `reject-${w.linkId}`} aria-label={`Reject ${w.email ?? w.teacher} as ${w.name}`} onClick={() => act(row, `reject-${w.linkId}`, () => api.post(`/admin/teachers/links/${w.linkId}/reject`), 'Rejected. They are told.')}>
                        Reject
                      </Button>
                    </span>
                  </div>
                  {w.heldBy && <Notice tone="warn">Also claimed by {w.heldBy}, who has this name now. Reject or detach that one first.</Notice>}
                  {others.length > 0 && <Notice>Also claimed by {others.join(', ')}.</Notice>}
                  {problem(row)}
                </div>
              )
            })}
          </div>
        </section>
      )}

      <section className="flex flex-col gap-2">
        <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">On Classy</h2>
        <div className="flex flex-col border-t-[2.5px] border-navy">
          {data.teachers.length === 0 && <p className="px-2 py-4 text-[15px] font-semibold text-muted">No teacher has signed in yet. They start from “Staff? Sign in here” on the sign-in screen.</p>}
          {data.teachers.map((t) => {
            const row = `t-${t.id}`
            const off = (key: string) => !!busy && busy !== key
            const pick = attach[t.id] ?? ''
            return (
              <article key={t.id} aria-label={t.name} className="flex flex-col gap-3 border-b-[1.5px] border-navy/10 px-2 py-5">
                <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
                  <div className="flex min-w-0 flex-col gap-1">
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="text-[20px] leading-tight font-extrabold [overflow-wrap:anywhere]">{t.name}</span>
                      {t.kind === 'student' && <Tag>Teaching assistant</Tag>}
                      {t.frozen && <Tag tone="alert">Frozen</Tag>}
                    </span>
                    <span className="text-[14px] font-semibold text-muted">
                      {t.email && <span className="font-mono font-bold break-all text-navy">{t.email}</span>}
                      {t.kind === 'student' &&
                        (t.studentId !== null && t.universityId ? (
                          <>
                            Student account{' '}
                            <Link to={`/admin/students/${t.studentId}`} className="font-mono font-bold text-navy link-hover">
                              {t.universityId}
                            </Link>
                          </>
                        ) : (
                          'Signs in with a student account'
                        ))}
                      {t.kind === 'staff' && ` · ${lastSeen(t.lastSeenAt)}`}
                      {t.createdBy && ` · added by ${t.createdBy}`}
                      {t.frozen && t.frozenAt && ` · frozen ${shortDate(t.frozenAt)}${t.frozenBy ? ` by ${t.frozenBy}` : ''}`}
                    </span>
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    {isOwner && (
                      <Button variant="secondary" size="sm" aria-label={`Open teacher view as ${t.name}`} onClick={() => view(t.key, t.name)}>
                        Open teacher view
                      </Button>
                    )}
                    <Button variant="secondary" size="sm" busy={busy === `freeze-${t.id}`} disabled={off(`freeze-${t.id}`)} aria-label={`${t.frozen ? 'Unfreeze' : 'Freeze'} ${t.name}`} onClick={() => act(row, `freeze-${t.id}`, () => api.put(`/admin/teachers/${t.id}/frozen`, { frozen: !t.frozen }), t.frozen ? 'Unfrozen.' : 'Frozen. They can’t change classes until you unfreeze them.')}>
                      {t.frozen ? 'Unfreeze' : 'Freeze'}
                    </Button>
                    {t.kind === 'staff' && (
                      <Button variant="secondary" size="sm" disabled={!!busy} aria-label={`Sign out ${t.name} everywhere`} onClick={() => setSigningOut(t)}>
                        Sign out everywhere
                      </Button>
                    )}
                    <Button variant="danger" size="sm" disabled={!!busy} aria-label={`Remove ${t.name}`} onClick={() => setRemoving(t)}>
                      Remove
                    </Button>
                  </div>
                </div>

                <Field label="Timetable names">
                  {t.names.length === 0 && <span className="text-[15px] font-semibold text-muted">None linked. Students see them as “{t.name}”.</span>}
                  {t.names.map((n) => (
                    <div key={n.linkId} className="flex flex-col gap-1.5">
                      <div className="flex flex-wrap items-center gap-x-2.5 gap-y-1">
                        <span className={cx('text-[16px] font-extrabold', n.state === 'rejected' && 'text-muted line-through')}>{n.name}</span>
                        <Tag tone={n.state === 'pending' ? 'sky' : n.state === 'rejected' ? 'plain' : 'ice'}>{n.state === 'approved' && n.how === 'staff' ? 'Attached by staff' : STATE[n.state]}</Tag>
                        {n.state !== 'rejected' && <span className="text-[14px] font-semibold text-muted">{plural(n.sections, 'section')}</span>}
                        {n.state === 'auto' && (
                          <RowButton busy={busy === `reverse-${n.linkId}`} disabled={off(`reverse-${n.linkId}`)} label={`Reverse the link of ${t.name} to ${n.name}`} onClick={() => act(row, `reverse-${n.linkId}`, () => api.post(`/admin/teachers/links/${n.linkId}/reject`), `Reversed. ${n.name} is nobody’s name now.`)}>
                            Reverse
                          </RowButton>
                        )}
                        {n.state === 'approved' && (
                          <RowButton busy={busy === `detach-${n.linkId}`} disabled={off(`detach-${n.linkId}`)} label={`Detach ${n.name} from ${t.name}`} onClick={() => act(row, `detach-${n.linkId}`, () => api.del(`/admin/teachers/${t.id}/names/${n.linkId}`), `Detached ${n.name}.`)}>
                            Detach
                          </RowButton>
                        )}
                      </div>
                      {n.suggestedName && (
                        <Notice className="self-start">
                          The timetable now has “{n.suggestedName}”. Same person?{' '}
                          <button type="button" disabled={!!busy} className="underline underline-offset-2" onClick={() => act(row, `same-${n.linkId}`, () => api.post(`/admin/teachers/links/${n.linkId}/same-person`), `Linked to ${n.suggestedName}.`)}>
                            Yes, same person
                          </button>
                        </Notice>
                      )}
                    </div>
                  ))}
                  {free.length > 0 && (
                    <div className="flex flex-wrap items-center gap-1.5 pt-1">
                      <Select aria-label={`Attach a name to ${t.name}`} value={pick} onChange={(e) => setAttach({ ...attach, [t.id]: e.target.value })} className="h-10 max-w-full min-w-0 rounded-[12px] text-[15px]">
                        <option value="">Attach a name…</option>
                        {free.map((n) => (
                          <option key={n.key} value={n.name}>
                            {n.name} ({n.sections})
                          </option>
                        ))}
                      </Select>
                      {pick && (
                        <Button
                          size="sm"
                          busy={busy === `attach-${t.id}`}
                          disabled={off(`attach-${t.id}`)}
                          onClick={() =>
                            act(
                              row,
                              `attach-${t.id}`,
                              async () => {
                                await api.post(`/admin/teachers/${t.id}/names`, { name: pick })
                                setAttach((a) => ({ ...a, [t.id]: '' }))
                              },
                              `Attached ${pick}.`,
                            )
                          }
                        >
                          Attach
                        </Button>
                      )}
                    </div>
                  )}
                </Field>

                {(t.added.length > 0 || t.hidden > 0) && (
                  <Field label="Classes they added">
                    {t.added.length > 0 && (
                      <ul className="flex flex-wrap gap-x-4 gap-y-1 text-[15px] font-semibold">
                        {t.added.map((a) => (
                          <li key={`${a.code}-${a.number}`}>
                            <span className="font-mono text-[14px] font-bold">
                              {a.code} {a.number}
                            </span>{' '}
                            <span className="text-muted">
                              {a.removed ? 'no longer on the timetable' : a.named ? `listed under ${a.named}` : 'no teacher listed'}
                              {a.by && `, added by ${a.by}`}
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                    {t.hidden > 0 && <span className="text-[14px] font-semibold text-muted">Hides {plural(t.hidden, 'class', 'classes')} of their own (“not mine”).</span>}
                  </Field>
                )}

                <div className="flex flex-wrap gap-x-5 gap-y-1 text-[15px] font-bold">
                  <Link to={`/admin/teacher-changes?teacher=${t.id}`} className={cx('link-hover', t.changesToday > 5 && 'text-alert')}>
                    {plural(t.changesToday, 'change')} today →
                  </Link>
                  <Link to={`/admin/teacher-changes?teacher=${t.id}`} className="link-hover">
                    {plural(t.upcomingChanges, 'upcoming change')} →
                  </Link>
                </div>
                {problem(row)}
              </article>
            )
          })}
        </div>
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">Timetable names</h2>
        <p className="text-[15px] font-semibold text-muted">
          Every teacher name in the live timetable, and the account linked to it.{isOwner && ' “View as” opens the teacher side as that name, with or without an account; what you change there is logged under you.'}
        </p>
        {data.names.length > 8 && <TextField label="Search names" hideLabel placeholder="Search names" value={q} onChange={(e) => setQ(e.target.value)} className="max-w-[360px]" />}
        {data.names.length === 0 ? (
          <p className="border-y-[1.5px] border-navy/[0.12] px-2 py-3.5 text-[15px] font-semibold text-muted">No semester is published, so there are no names yet.</p>
        ) : (
          // Positioned, so the hidden "Actions" heading scrolls with the table instead of widening the page.
          <div className="relative overflow-x-auto">
            <table className="w-full min-w-[560px] border-collapse text-[15px]">
              <thead>
                <tr className="border-b-[2.5px] border-navy">
                  <Th>Name</Th>
                  <Th right>Sections</Th>
                  <Th>Account</Th>
                  {isOwner && (
                    <th scope="col">
                      <span className="sr">Actions</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {names.map((n) => {
                  const holder = holderOf(n)
                  return (
                    <tr key={n.key} className="tint border-b-[1.5px] border-navy/10">
                      <td className="px-2 py-3 font-extrabold">
                        <span className="flex flex-wrap items-center gap-2">
                          {n.name}
                          {n.placeholder && <Tag tone="plain">Placeholder</Tag>}
                        </span>
                      </td>
                      <td className="px-2 py-3 text-right font-bold tabular-nums">{n.sections}</td>
                      <td className="px-2 py-3 font-semibold [overflow-wrap:anywhere]">{holder ? label(holder) : <span className="text-muted">{n.placeholder ? 'Can’t be claimed' : 'Nobody yet'}</span>}</td>
                      {isOwner && (
                        <td className="px-2 py-3 text-right whitespace-nowrap">
                          <Button variant="secondary" size="sm" aria-label={`View as ${n.name}`} onClick={() => view(n.key, n.name)}>
                            View as
                          </Button>
                        </td>
                      )}
                    </tr>
                  )
                })}
              </tbody>
            </table>
            {names.length === 0 && <p className="mt-4 text-[15px] font-semibold">No name matches “{q.trim()}”.</p>}
          </div>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">Blocked emails</h2>
        <p className="text-[15px] font-semibold text-muted">Removing a teacher blocks their email. It can’t sign in again until it is unblocked here.</p>
        <div className="flex flex-col border-t-[2.5px] border-navy">
          {data.blocked.length === 0 && <p className="border-b-[1.5px] border-navy/10 px-2 py-4 text-[15px] font-semibold text-muted">None.</p>}
          {data.blocked.map((b) => {
            const row = `b-${b.email}`
            return (
              <div key={b.email} className="tint flex flex-col gap-2 border-b-[1.5px] border-navy/10 px-2 py-4">
                <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
                  <span className="flex min-w-0 flex-col gap-0.5">
                    <span className="font-mono text-[16px] font-bold break-all">{b.email}</span>
                    <span className="text-[14px] font-semibold text-muted">
                      Blocked {shortDate(b.blockedAt)}
                      {b.by && ` by ${b.by}`}
                    </span>
                  </span>
                  <Button variant="secondary" size="sm" busy={busy === row} disabled={!!busy && busy !== row} aria-label={`Unblock ${b.email}`} onClick={() => act(row, row, () => api.post('/admin/teachers/unblock', { email: b.email }), 'Unblocked. They can sign in again.')}>
                    Unblock
                  </Button>
                </div>
                {problem(row)}
              </div>
            )
          })}
        </div>
      </section>

      <RemoveTeacher
        teacher={removing}
        onClose={() => setRemoving(null)}
        onRemoved={async (undone) => {
          toast.show(undone ? `Removed. ${plural(undone, 'change')} undone.` : 'Removed.')
          await load()
        }}
      />
      <Confirm
        open={!!signingOut}
        onClose={() => setSigningOut(null)}
        title="Sign out everywhere?"
        body={`${signingOut?.email ?? signingOut?.name} is signed out on every device at their next tap and needs a new emailed code to come back. Their names, classes and office hours stay.`}
        confirmLabel="Sign out everywhere"
        onConfirm={async () => {
          if (!signingOut) return
          await api.post(`/admin/teachers/${signingOut.id}/sign-out`)
          toast.show('Signed out everywhere.')
        }}
      />
      <Confirm
        open={switching}
        onClose={() => setSwitching(false)}
        title={data.changesEnabled ? 'Switch off teacher class changes?' : 'Switch teacher class changes back on?'}
        body={
          data.changesEnabled
            ? 'No teacher can cancel, move, re-room or add a class until an owner switches this back on. Changes already made stay, and undo still works.'
            : 'Teachers can cancel, move, re-room and add classes again. Frozen teachers stay frozen.'
        }
        confirmLabel={data.changesEnabled ? 'Switch off' : 'Switch on'}
        danger={data.changesEnabled}
        onConfirm={async () => {
          const r = await api.put<{ changesEnabled: boolean }>('/admin/teachers/switch', { enabled: !data.changesEnabled })
          toast.show(r.changesEnabled ? 'Teacher class changes are on.' : 'Teacher class changes are off.')
          await load()
        }}
      />
      <Toast toast={toast} className="bottom-7 left-1/2 max-w-[calc(100vw-2rem)] -translate-x-1/2 rounded-full bg-navy px-[22px] py-3.5 text-center text-[15px] font-extrabold text-cream shadow-[0_12px_24px_-12px_rgb(11_25_87/0.6)]" />
    </>
  )
}

/** A labelled block inside a teacher's row. */
function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <span className="label text-[11px] tracking-[0.12em] text-muted">{label}</span>
      {children}
    </div>
  )
}

/** The small text button beside a name: Reverse, Detach. */
function RowButton({ busy, disabled, label, onClick, children }: { busy: boolean; disabled: boolean; label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" disabled={busy || disabled} aria-busy={busy || undefined} aria-label={label} onClick={onClick} className="label press flex h-9 items-center text-[11px] link-hover">
      {busy ? 'Working…' : children}
    </button>
  )
}

/** Remove a teacher: blocks a staff email, and offers to undo the changes that can still be undone. */
function RemoveTeacher({ teacher, onClose, onRemoved }: { teacher: Teacher | null; onClose: () => void; onRemoved: (undone: number) => Promise<void> }) {
  const [undo, setUndo] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  // Each teacher's dialog starts unticked and without the last one's error.
  const [shownFor, setShownFor] = useState<number | null>(null)
  if (teacher && teacher.id !== shownFor) {
    setShownFor(teacher.id)
    setUndo(false)
    setError('')
  }
  const t = teacher
  return (
    <Modal open={!!t} onClose={onClose} label={t ? `Remove ${t.name}?` : 'Remove teacher'}>
      {t && (
        <>
          <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em] [overflow-wrap:anywhere]">Remove {t.name}?</h2>
          <p className="text-[16px] leading-snug font-semibold text-muted">
            {t.kind === 'staff'
              ? `${t.email} is signed out and blocked: that email can’t sign in again until someone unblocks it here. Their linked names, added classes and office hours are deleted.`
              : 'This takes their teaching rights away. Their student account stays, and their linked names, added classes and office hours are deleted.'}{' '}
            The log keeps the changes they made.
          </p>
          {t.upcomingChanges > 0 && (
            <label className="flex cursor-pointer items-start gap-3 rounded-[14px] bg-paper p-3">
              <Checkbox checked={undo} onChange={setUndo} label={`Also undo their ${plural(t.upcomingChanges, 'upcoming change')}`} />
              <span className="text-[15px] leading-snug font-bold">
                Also undo their {plural(t.upcomingChanges, 'upcoming change')} <span className="font-semibold text-muted">(students are told the classes are back on)</span>
              </span>
            </label>
          )}
          <ErrorText>{error}</ErrorText>
          <div className="flex justify-end gap-2.5">
            <Button variant="secondary" onClick={onClose}>
              Cancel
            </Button>
            <Button
              className="bg-alert! text-cream!"
              busy={busy}
              onClick={async () => {
                setBusy(true)
                setError('')
                try {
                  const r = await api.post<{ undone: number }>(`/admin/teachers/${t.id}/remove`, { undoChanges: undo })
                  onClose()
                  await onRemoved(r.undone)
                } catch (e) {
                  setError(errorText(e))
                } finally {
                  setBusy(false)
                }
              }}
            >
              {busy ? 'Working…' : t.kind === 'staff' ? 'Remove and block' : 'Remove'}
            </Button>
          </div>
        </>
      )}
    </Modal>
  )
}
