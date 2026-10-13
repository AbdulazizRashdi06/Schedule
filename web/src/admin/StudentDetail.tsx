import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Section, Semester } from '../lib/types'
import { minutes } from '../lib/time'
import { SLOTS, sessionType, slotOf, type ClassType } from '../lib/slots'
import { Button, Confirm, ErrorText, Loading, Tag, cx } from '../components/ui'
import { shortDate, seen } from './AdminLayout'
import { SetupTag } from './Students'

interface Detail {
  id: number
  universityId: string
  email: string
  isOwner: boolean
  year: number | null
  createdAt: string
  lastSeenAt: string | null
  setupCompletedAt: string | null
  major: { code: string; name: string } | null
  step: string
  done: boolean
  semester: Semester | null
  picks: { courseId: number; code: string; name: string; type: string; section: Section | null; sectionRemoved: boolean }[]
  clashes: [number, number][]
}

type Action = 'major' | 'picks' | 'remove' | 'owner' | null

/** On a cream page lecture blocks are navy so they stay visible; labs are ice, tutorials sky. */
const CELL: Record<ClassType, string> = { lecture: 'bg-navy text-cream', lab: 'bg-ice text-navy', tutorial: 'bg-sky text-navy' }
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** "today at 09:52", "yesterday", "on 8 Oct". */
function lastSeen(iso: string) {
  const s = seen(iso)
  return s.startsWith('Today') ? s.replace('Today', 'today at') : s === 'Yesterday' ? 'yesterday' : `on ${s}`
}

export default function StudentDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [d, setD] = useState<Detail | null>(null)
  const [error, setError] = useState('')
  const [action, setAction] = useState<Action>(null)
  const load = () => api.get<Detail>(`/admin/students/${id}`).then(setD, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!d) return <Loading />
  const clashing = new Set(d.clashes.flat())

  const actions: Record<Exclude<Action, null>, { title: string; body: string; label: string; danger?: boolean; run: () => Promise<unknown> }> = {
    major: { title: 'Reset major?', body: `${d.universityId} will pick their major, year and courses again at their next visit.`, label: 'Reset major', run: () => api.post(`/admin/students/${d.id}/reset`, { what: 'major' }) },
    picks: { title: 'Reset picks?', body: `${d.universityId} keeps their major and year but picks their courses again at their next visit.`, label: 'Reset picks', run: () => api.post(`/admin/students/${d.id}/reset`, { what: 'picks' }) },
    remove: { title: 'Remove student?', body: `This deletes ${d.universityId} and their picks. They can sign up again with their ID.`, label: 'Remove', danger: true, run: () => api.del(`/admin/students/${d.id}`) },
    owner: d.isOwner
      ? { title: 'Remove owner access?', body: `${d.universityId} will no longer see the dashboard.`, label: 'Remove access', danger: true, run: () => api.put(`/admin/students/${d.id}/owner`, { isOwner: false }) }
      : { title: 'Make owner?', body: `${d.universityId} will see the full dashboard, including every student. Only do this for partners running the app.`, label: 'Make owner', run: () => api.put(`/admin/students/${d.id}/owner`, { isOwner: true }) },
  }
  const a = action ? actions[action] : null

  // The read-only week: slot rows × day columns.
  const cells = new Map<string, { code: string; type: ClassType; clash: boolean }[]>()
  for (const p of d.picks)
    for (const m of p.section?.meetings ?? []) {
      const key = `${m.day}-${slotOf(minutes(m.start), minutes(m.end))}`
      cells.set(key, [...(cells.get(key) ?? []), { code: p.code, type: sessionType(m.room), clash: clashing.has(p.section!.id) }])
    }
  const days = [0, 1, 2, 3, 4, 5, 6].filter((day) => day <= 4 || [...cells.keys()].some((k) => k.startsWith(`${day}-`)))

  const status = [d.done && d.setupCompletedAt ? `Setup done ${shortDate(d.setupCompletedAt)}.` : `Joined ${shortDate(d.createdAt)}.`, d.lastSeenAt ? `Last seen ${lastSeen(d.lastSeenAt)}.` : 'Never signed in.']

  return (
    <>
      <Link to="/admin/students" className="label self-start text-[12px] link-hover">
        ← All students
      </Link>
      <div className="flex flex-col gap-2">
        <h1 className="font-mono text-[56px] leading-none font-bold tracking-[-0.02em]">{d.universityId}</h1>
        <span className="flex flex-wrap items-center gap-2 text-[17px] font-bold">
          {d.major ? `${d.major.name}${d.year != null ? `, Year ${d.year}` : ''}` : 'No major yet'}
          <SetupTag step={d.step} done={d.done} />
          {d.isOwner && <Tag tone="navy">Owner</Tag>}
        </span>
        <span className="text-[15px] font-semibold text-muted">
          {status.join(' ')} <span className="break-all">{d.email}</span>
        </span>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" size="sm" onClick={() => setAction('major')}>
          Reset major
        </Button>
        <Button variant="secondary" size="sm" onClick={() => setAction('picks')}>
          Reset picks
        </Button>
        <Button variant="secondary" size="sm" onClick={() => setAction('owner')}>
          {d.isOwner ? 'Remove owner access' : 'Make owner'}
        </Button>
        <Button variant="danger" size="sm" onClick={() => setAction('remove')}>
          Remove student
        </Button>
      </div>
      <div className="flex flex-wrap items-start gap-10">
        <section className="flex flex-[1_1_300px] flex-col">
          <h2 className="mb-2.5 text-[24px] font-extrabold">Picks {d.semester && <span className="label text-[12px] text-muted">{d.semester.name}</span>}</h2>
          {d.picks.length === 0 && <p className="text-[15px] font-semibold text-muted">No courses picked{d.semester ? '' : ' (no semester published)'}.</p>}
          {d.picks.map((p, i) => (
            <div key={p.courseId} className={cx('flex items-center gap-3 py-2.5', i > 0 && 'border-t-[1.5px] border-navy/10')}>
              <span className="w-[52px] shrink-0 truncate rounded-[10px] bg-ice px-1 py-1.5 text-center font-mono text-[12px] font-bold">{p.code}</span>
              <span className="flex min-w-0 flex-1 flex-col gap-px">
                <span className="text-[16px] font-extrabold">{p.name}</span>
                <span className={cx('font-mono text-[11px] font-bold uppercase', p.section ? 'text-muted' : 'text-alert')}>
                  {p.section ? `${p.type}, section ${p.section.number}` : p.sectionRemoved ? 'Section removed, needs to re-pick' : 'No section'}
                  {p.section && clashing.has(p.section.id) && <span className="text-alert">, clash</span>}
                </span>
              </span>
            </div>
          ))}
        </section>
        <section className="flex flex-[2_1_460px] flex-col gap-2.5">
          <h2 className="text-[24px] font-extrabold">Timetable</h2>
          <div role="table" aria-label="Weekly timetable" className="grid gap-1.5" style={{ gridTemplateColumns: `56px repeat(${days.length}, minmax(0, 1fr))` }}>
            <div role="row" className="contents">
              <span role="columnheader" />
              {days.map((day) => (
                <span key={day} role="columnheader" className="label pb-1 text-center text-[11px] tracking-normal">
                  {DAYS[day]}
                </span>
              ))}
            </div>
            {SLOTS.map(([start], slot) => (
              <div key={slot} role="row" className="contents">
                <span role="rowheader" className="flex h-11 items-center font-mono text-[11px] font-bold">
                  {start}
                </span>
                {days.map((day) => {
                  const here = cells.get(`${day}-${slot}`) ?? []
                  const clash = here.length > 1 || here.some((c) => c.clash)
                  return (
                    <span
                      key={day}
                      role="cell"
                      className={cx(
                        'flex h-11 items-center justify-center truncate rounded-[12px] px-1 font-mono text-[12px] font-bold',
                        here.length ? CELL[here[0].type] : 'shadow-[inset_0_0_0_1.5px_rgb(11_25_87/0.1)]',
                        clash && 'outline-2 -outline-offset-2 outline-alert',
                      )}
                    >
                      {here.map((c) => c.code).join('/')}
                    </span>
                  )
                })}
              </div>
            ))}
          </div>
        </section>
      </div>
      <Confirm
        open={!!a}
        onClose={() => setAction(null)}
        title={a?.title ?? ''}
        body={a?.body}
        confirmLabel={a?.label ?? ''}
        danger={a?.danger}
        onConfirm={async () => {
          await a!.run()
          if (action === 'remove') navigate('/admin/students', { replace: true })
          else await load()
        }}
      />
    </>
  )
}
