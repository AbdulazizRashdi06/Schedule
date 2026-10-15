import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, Meeting, Section, Semester, Weeks } from '../lib/types'
import { DAY_LONG, meetsText } from '../lib/time'
import { SLOTS } from '../lib/slots'
import { yearName } from '../lib/year'
import { Button, Drawer, ErrorText, Loading, Notice, Select, Tag, TextField, Toast, useToast } from '../components/ui'
import { PageTitle, Th, shortDate } from './AdminLayout'
import PublicNotes from './PublicNotes'

interface Detail {
  course: Course
  semester: Semester
  enrollment: Record<string, number>
  noSection: number
  weeks: Weeks
}

const meets = (s: Section) => meetsText(s, ', ')
const rooms = (s: Section) => [...new Set(s.meetings.map((m) => m.room).filter(Boolean))].join(', ')

export default function CourseDetail() {
  const { id } = useParams()
  const [d, setD] = useState<Detail | null>(null)
  const [editing, setEditing] = useState<Section | null>(null)
  const [savedIds, setSavedIds] = useState<Set<number>>(new Set())
  const [error, setError] = useState('')
  const toast = useToast()
  const load = () => api.get<Detail>(`/admin/courses/${id}`).then(setD, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!d) return <Loading />
  const c = d.course
  const total = Object.values(d.enrollment).reduce((a, b) => a + b, 0) + d.noSection
  const students = (s: Section) => d.enrollment[s.id] ?? 0

  return (
    <>
      <Link to="/admin/courses" className="label self-start text-[12px] link-hover">
        ← All courses
      </Link>
      <PageTitle
        size={64}
        kicker={[c.code, c.majors.join(', ')].filter(Boolean).join(' · ')}
        title={c.name}
        sub={[c.type === 'LS' ? 'Life Skills' : c.type, c.year != null && yearName(c.year), c.credits && `${c.credits} credits`, `${total} ${total === 1 ? 'student' : 'students'} across ${c.sections.length} ${c.sections.length === 1 ? 'section' : 'sections'}`]
          .filter(Boolean)
          .join(', ')}
      />
      {!d.semester.isCurrent && <Notice>{d.semester.name} isn’t published, so edits here don’t show a “Changed” badge to students.</Notice>}
      {d.noSection > 0 && (
        <Notice tone="warn">
          {d.noSection} {d.noSection === 1 ? 'student takes' : 'students take'} this course without a section, for example because theirs was removed.
        </Notice>
      )}
      <div className="overflow-x-auto">
        <table className="w-full min-w-[720px] border-collapse text-[15px]">
          <thead>
            <tr className="border-b-[2.5px] border-navy">
              <Th>Section</Th>
              <Th>Meets</Th>
              <Th>Room</Th>
              <Th>Instructor</Th>
              <Th right>Students</Th>
              <th scope="col">
                <span className="sr">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {c.sections.map((s) => (
              <tr key={s.id} className="tint border-b-[1.5px] border-navy/10 align-top">
                <td className="px-2 py-3.5 text-[22px] font-extrabold">{s.number}</td>
                <td className="px-2 py-3.5 font-bold">
                  {meets(s)}
                  {s.changedAt && s.changeSummary && (
                    <span className="block pt-1 text-[13px] font-semibold text-muted">
                      Changed {shortDate(s.changedAt)}: {s.changeSummary}
                    </span>
                  )}
                </td>
                <td className="px-2 py-3.5 font-bold">{rooms(s) || <Tag tone="alert">No room</Tag>}</td>
                <td className="px-2 py-3.5 font-semibold">{s.instructor || <span className="text-muted">Not set</span>}</td>
                <td className="px-2 py-3.5 text-right font-bold tabular-nums">{students(s)}</td>
                <td className="px-2 py-3.5 text-right whitespace-nowrap">
                  {savedIds.has(s.id) && (
                    <Tag className="mr-2" tone="ice">
                      Saved
                    </Tag>
                  )}
                  <Button variant="secondary" size="sm" aria-label={`Edit section ${s.number}`} onClick={() => setEditing(s)}>
                    Edit
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <PublicNotes courseId={c.id} />

      <Drawer open={!!editing} onClose={() => setEditing(null)} label="Edit section">
        {editing && (
          <SectionEditor
            key={editing.id}
            code={c.code}
            section={editing}
            students={students(editing)}
            live={d.semester.isCurrent}
            weeks={d.weeks}
            onCancel={() => setEditing(null)}
            onSaved={async (changed) => {
              const n = students(editing)
              setEditing(null)
              setSavedIds(new Set(savedIds).add(editing.id))
              toast.show(changed && d.semester.isCurrent ? `Section ${editing.number} saved. ${n} ${n === 1 ? 'student' : 'students'} notified.` : `Section ${editing.number} saved.`)
              await load()
            }}
          />
        )}
      </Drawer>
      <Toast
        toast={toast}
        className="bottom-7 left-1/2 -translate-x-1/2 rounded-full bg-navy px-[22px] py-3.5 text-[15px] font-extrabold whitespace-nowrap text-cream shadow-[0_12px_24px_-12px_rgb(11_25_87/0.6)]"
      />
    </>
  )
}

type Row = Meeting & { key: number }

let nextKey = 1
const withKey = (m: Meeting): Row => ({ ...m, key: nextKey++ })
const DAY_SHORT_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const parse = (s: string) => {
  const [y, m, d] = s.split('-').map(Number)
  return new Date(y, m - 1, d)
}
const addDays = (s: string, n: number) => {
  const d = parse(s)
  return iso(new Date(d.getFullYear(), d.getMonth(), d.getDate() + n))
}
/** The Sunday that starts a date's week, as "2026-09-20". */
const weekOf = (date: string) => addDays(date, -parse(date).getDay())
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const fmt = (d: Date) => `${d.getDate()} ${MONTHS[d.getMonth()]}`
/** "Week 4" from the semester's teaching weeks, or "Week of" when they aren't known. */
const weekName = (sunday: string, w: Weeks) => {
  if (!w.week1Start) return 'Week of'
  const n = Math.round((parse(sunday).getTime() - parse(w.week1Start).getTime()) / (7 * 86_400_000)) + 1
  return n < 1 ? 'Before week 1' : w.lastWeek && n > w.lastWeek ? `After week ${w.lastWeek}` : `Week ${n}`
}
/** 24-hour "08:00". */
const TIME = /^([01]\d|2[0-3]):[0-5]\d$/
const order = (a: Meeting, b: Meeting) => (a.date ?? '').localeCompare(b.date ?? '') || a.day - b.day || a.start.localeCompare(b.start)
const plain = (list: Meeting[]) => [...list].sort(order).map(({ day, start, end, room, date }) => ({ day, start, end, room, date }))

/** Every class of a section: weekly ones first, then dated classes grouped by week, with past weeks folded away. */
function SectionEditor({ code, section, students, live, weeks: teaching, onCancel, onSaved }: { code: string; section: Section; students: number; live: boolean; weeks: Weeks; onCancel: () => void; onSaved: (changed: boolean) => void }) {
  const [instructor, setInstructor] = useState(section.instructor)
  const [rows, setRows] = useState<Row[]>(() => section.meetings.map(withKey).sort(order))
  const [showPast, setShowPast] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const update = (key: number, patch: Partial<Meeting>) =>
    setRows(rows.map((r) => (r.key === key ? { ...r, ...patch, ...(patch.date ? { day: parse(patch.date).getDay() } : {}) } : r)))
  const remove = (key: number) => setRows(rows.filter((r) => r.key !== key))

  const thisWeek = weekOf(iso(new Date()))
  const weekly = rows.filter((r) => !r.date)
  const dated = rows.filter((r) => r.date)
  const weeks = [...new Set(dated.map((r) => weekOf(r.date!)))].sort()
  const past = weeks.filter((w) => w < thisWeek)
  const shown = showPast ? weeks : weeks.filter((w) => w >= thisWeek)

  /** A new dated class a week after the last one, at the same time and room (today, first slot, when there are none). */
  const addDated = () => {
    const last = [...dated].sort(order)[dated.length - 1] as Row | undefined
    const date = last ? addDays(last.date!, 7) : iso(new Date())
    setRows([...rows, withKey({ day: parse(date).getDay(), start: last?.start ?? SLOTS[0][0], end: last?.end ?? SLOTS[0][1], room: last?.room ?? '', date })].sort(order))
  }
  const addWeekly = () => setRows([...rows, withKey({ day: 0, start: SLOTS[0][0], end: SLOTS[0][1], room: weekly[weekly.length - 1]?.room ?? '', date: null })].sort(order))

  const row = (r: Row) => (
    <div key={r.key} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.2fr)_auto] items-center gap-1.5">
      {r.date ? (
        <TextField label="Date" hideLabel type="date" value={r.date} onChange={(e) => e.target.value && update(r.key, { date: e.target.value })} className="col-span-3" boxClassName="h-11 px-2.5" />
      ) : (
        <Select aria-label="Day" value={r.day} onChange={(e) => update(r.key, { day: Number(e.target.value) })} className="col-span-3 h-11">
          {DAY_LONG.map((name, k) => (
            <option key={k} value={k}>
              Every {name}
            </option>
          ))}
        </Select>
      )}
      <button type="button" onClick={() => remove(r.key)} aria-label="Remove this class" className="press grid size-9 place-items-center rounded-full text-[20px] font-bold text-alert">
        ×
      </button>
      <TextField label="Start" hideLabel mono inputMode="numeric" placeholder="08:00" value={r.start} invalid={!TIME.test(r.start)} onChange={(e) => update(r.key, { start: e.target.value })} boxClassName="h-11 px-2.5" />
      <TextField label="End" hideLabel mono inputMode="numeric" placeholder="09:30" value={r.end} invalid={!TIME.test(r.end) || r.end <= r.start} onChange={(e) => update(r.key, { end: e.target.value })} boxClassName="h-11 px-2.5" />
      <TextField label="Room" hideLabel placeholder="Room" value={r.room} invalid={!r.room.trim()} onChange={(e) => update(r.key, { room: e.target.value })} className="col-span-2" boxClassName="h-11 px-2.5" />
    </div>
  )

  return (
    <form
      className="flex min-h-full flex-col gap-[18px]"
      onSubmit={async (e) => {
        e.preventDefault()
        if (rows.length === 0) return setError('Keep at least one class.')
        setBusy(true)
        setError('')
        try {
          const meetings = plain(rows)
          await api.put(`/admin/sections/${section.id}`, { instructor, meetings })
          onSaved(JSON.stringify([section.instructor, plain(section.meetings)]) !== JSON.stringify([instructor.trim(), meetings]))
        } catch (err) {
          setError(errorText(err))
          setBusy(false)
        }
      }}
    >
      <div className="flex items-baseline justify-between">
        <h2 className="text-[34px] font-extrabold tracking-[-0.03em]">Section {section.number}</h2>
        <span className="label text-[12px] tracking-normal">{code}</span>
      </div>
      <TextField label="Instructor" value={instructor} onChange={(e) => setInstructor(e.target.value)} />

      {weekly.length > 0 && (
        <section className="flex flex-col gap-2">
          <h3 className="label text-[11px] tracking-[0.12em]">Every week</h3>
          {weekly.map(row)}
        </section>
      )}

      {past.length > 0 && (
        <button type="button" className="label self-start text-[11px] link-hover" onClick={() => setShowPast(!showPast)}>
          {showPast ? 'Hide past classes' : `Show past classes (${past.length} ${past.length === 1 ? 'week' : 'weeks'})`}
        </button>
      )}
      {shown.map((w) => {
        const list = dated.filter((r) => weekOf(r.date!) === w)
        return (
          <section key={w} className="flex flex-col gap-2">
            <h3 className="label flex justify-between text-[11px] tracking-[0.12em]">
              <span>
                {teaching.week1Start ? `${weekName(w, teaching)} · ` : 'Week of '}
                {fmt(parse(w))}
                {w === thisWeek && ' · this week'}
              </span>
              <span className="text-muted">{list.map((r) => DAY_SHORT_NAMES[r.day]).join(' ')}</span>
            </h3>
            {list.map(row)}
          </section>
        )
      })}
      {dated.length > 0 && shown.length === 0 && <p className="text-[14px] font-semibold text-muted">No classes left this semester.</p>}

      <div className="flex flex-wrap gap-x-4 gap-y-1">
        <button type="button" className="label text-[11px] link-hover" onClick={addDated}>
          + Add a class on a date
        </button>
        <button type="button" className="label text-[11px] link-hover" onClick={addWeekly}>
          + Add a weekly class
        </button>
      </div>
      <span className="text-[14px] font-semibold text-muted">
        {live
          ? `${students} ${students === 1 ? 'student' : 'students'} in this section will see the change in the app.`
          : 'This semester isn’t published, so nobody is notified.'}
      </span>
      <ErrorText>{error}</ErrorText>
      <div className="sticky bottom-0 mt-auto flex gap-2.5 bg-cream pt-2">
        <Button type="button" variant="secondary" className="h-[52px]" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" className="h-[52px] flex-1" busy={busy}>
          {busy ? 'Saving…' : 'Save section'}
        </Button>
      </div>
    </form>
  )
}
