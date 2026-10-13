import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, Meeting, Section, Semester } from '../lib/types'
import { DAY_LONG, DAY_SHORT } from '../lib/time'
import { SLOTS } from '../lib/slots'
import { Button, Drawer, ErrorText, Loading, Notice, Select, Tag, TextField, Toast, useToast } from '../components/ui'
import { PageTitle, Th, shortDate } from './AdminLayout'

interface Detail {
  course: Course
  semester: Semester
  enrollment: Record<string, number>
  noSection: number
}

const meets = (s: Section) => s.meetings.map((m) => `${DAY_SHORT[m.day]} ${m.start}`).join(', ')
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
        sub={[c.type === 'LS' ? 'Life Skills' : c.type, c.year && `Year ${c.year}`, c.credits && `${c.credits} credits`, `${total} ${total === 1 ? 'student' : 'students'} across ${c.sections.length} ${c.sections.length === 1 ? 'section' : 'sections'}`]
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

      <Drawer open={!!editing} onClose={() => setEditing(null)} label="Edit section">
        {editing && (
          <SectionEditor
            key={editing.id}
            code={c.code}
            section={editing}
            students={students(editing)}
            live={d.semester.isCurrent}
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

type Row = Meeting

/** The slot a meeting starts in, or "custom" for times that aren't a class slot. */
const slotOf = (m: Row) => {
  const i = SLOTS.findIndex(([s, e]) => s === m.start && e === m.end)
  return i < 0 ? 'custom' : String(i)
}

function SectionEditor({ code, section, students, live, onCancel, onSaved }: { code: string; section: Section; students: number; live: boolean; onCancel: () => void; onSaved: (changed: boolean) => void }) {
  const [instructor, setInstructor] = useState(section.instructor)
  const [rows, setRows] = useState<Row[]>(section.meetings.map((m) => ({ ...m })))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const update = (i: number, patch: Partial<Row>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  return (
    <form
      className="flex min-h-full flex-col gap-[18px]"
      onSubmit={async (e) => {
        e.preventDefault()
        setBusy(true)
        setError('')
        try {
          const before = JSON.stringify([section.instructor, section.meetings])
          await api.put(`/admin/sections/${section.id}`, { instructor, meetings: rows })
          onSaved(before !== JSON.stringify([instructor.trim(), rows]))
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
      {rows.map((r, i) => (
        <fieldset key={i} className="flex flex-col gap-1.5">
          <legend className="label flex w-full items-center justify-between pb-1.5 text-[11px] tracking-[0.12em]">
            Class {i + 1}
            {rows.length > 1 && (
              <button type="button" onClick={() => setRows(rows.filter((_, j) => j !== i))} className="label text-[11px] text-alert link-hover" aria-label={`Remove class ${i + 1}`}>
                Remove
              </button>
            )}
          </legend>
          <div className="grid grid-cols-2 gap-2">
            <Select aria-label={`Class ${i + 1} day`} value={r.day} onChange={(e) => update(i, { day: Number(e.target.value) })}>
              {DAY_LONG.map((name, k) => (
                <option key={k} value={k}>
                  {name}
                </option>
              ))}
            </Select>
            <Select
              aria-label={`Class ${i + 1} start`}
              value={slotOf(r)}
              onChange={(e) => {
                const slot = SLOTS[Number(e.target.value)]
                if (slot) update(i, { start: slot[0], end: slot[1] })
              }}
            >
              {SLOTS.map(([s], k) => (
                <option key={k} value={k}>
                  {s}
                </option>
              ))}
              {slotOf(r) === 'custom' && (
                <option value="custom" disabled>
                  {r.start}–{r.end}
                </option>
              )}
            </Select>
          </div>
          <TextField label={`Class ${i + 1} room`} hideLabel placeholder="Room" value={r.room} invalid={!r.room.trim()} onChange={(e) => update(i, { room: e.target.value })} />
        </fieldset>
      ))}
      <button
        type="button"
        className="label self-start text-[11px] link-hover"
        onClick={() => setRows([...rows, { ...(rows[rows.length - 1] ?? { day: 0, start: SLOTS[0][0], end: SLOTS[0][1], room: '' }) }])}
      >
        + Add a class
      </button>
      <TextField label="Instructor" value={instructor} onChange={(e) => setInstructor(e.target.value)} />
      <span className="text-[14px] font-semibold text-muted">
        {live
          ? `${students} ${students === 1 ? 'student' : 'students'} in this section will see the change in the app.`
          : 'This semester isn’t published, so nobody is notified.'}
      </span>
      <ErrorText>{error}</ErrorText>
      <div className="mt-auto flex gap-2.5">
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
