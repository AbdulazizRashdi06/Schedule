import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, Meeting, Section, Semester } from '../lib/types'
import { DAY_SHORT, sectionSummary } from '../lib/time'
import { Badge, Button, Card, ErrorText, Input, Loading, Notice, Select, XIcon } from '../components/ui'
import { PageTitle } from './AdminLayout'

interface Detail {
  course: Course
  semester: Semester
  enrollment: Record<string, number>
  noSection: number
}

export default function CourseDetail() {
  const { id } = useParams()
  const [d, setD] = useState<Detail | null>(null)
  const [editing, setEditing] = useState<number | null>(null)
  const [error, setError] = useState('')
  const load = () => api.get<Detail>(`/admin/courses/${id}`).then(setD, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!d) return <Loading />
  const c = d.course
  const total = Object.values(d.enrollment).reduce((a, b) => a + b, 0) + d.noSection

  return (
    <>
      <Link to="/admin/courses" className="mb-3 inline-block text-sm text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200">
        ← All courses
      </Link>
      <PageTitle
        title={`${c.code} · ${c.name}`}
        sub={
          <span className="flex flex-wrap items-center gap-2">
            <Badge tone={c.type === 'Core' ? 'indigo' : c.type === 'LS' ? 'green' : 'amber'}>{c.type}</Badge>
            {c.year && <span>Year {c.year}</span>}
            {c.majors.length > 0 && <span>{c.majors.join(', ')}</span>}
            {c.credits && <span>{c.credits} credits</span>}
            <span>· {d.semester.name}</span>
            <span>· {total} student{total === 1 ? '' : 's'}</span>
          </span>
        }
      />
      {!d.semester.isCurrent && <div className="mb-4"><Notice>This semester isn't published, so edits here don't show a “Changed” badge to students.</Notice></div>}
      {d.noSection > 0 && <div className="mb-4"><Notice tone="warn">{d.noSection} student{d.noSection === 1 ? '' : 's'} took this course without a section (e.g. theirs was removed).</Notice></div>}
      <div className="grid gap-3 md:grid-cols-2">
        {c.sections.map((s) =>
          editing === s.id ? (
            <SectionEditor
              key={s.id}
              section={s}
              onCancel={() => setEditing(null)}
              onSaved={async () => {
                setEditing(null)
                await load()
              }}
            />
          ) : (
            <Card key={s.id} className="p-4">
              <div className="flex items-start justify-between gap-2">
                <div>
                  <div className="font-semibold">Section {s.number}</div>
                  <div className="text-sm text-zinc-500">{s.instructor || 'No instructor'}</div>
                </div>
                <div className="text-right">
                  <div className="text-xl font-semibold tabular-nums">{d.enrollment[s.id] ?? 0}</div>
                  <div className="text-xs text-zinc-500">{(d.enrollment[s.id] ?? 0) === 1 ? 'student' : 'students'}</div>
                </div>
              </div>
              <ul className="mt-3 space-y-1 text-sm">
                {s.meetings.map((m, i) => (
                  <li key={i} className="flex justify-between">
                    <span>
                      {DAY_SHORT[m.day]} {m.start}–{m.end}
                    </span>
                    <span className="text-zinc-500">{m.room || '—'}</span>
                  </li>
                ))}
              </ul>
              {s.changedAt && (
                <p className="mt-3 text-xs text-amber-700 dark:text-amber-400">
                  Changed {new Date(s.changedAt).toLocaleString()}: {s.changeSummary}
                </p>
              )}
              <Button variant="secondary" className="mt-3 h-9 w-full text-sm" onClick={() => setEditing(s.id)}>
                Edit section
              </Button>
            </Card>
          ),
        )}
      </div>
    </>
  )
}

type Row = Omit<Meeting, 'day'> & { day: number }

function SectionEditor({ section, onCancel, onSaved }: { section: Section; onCancel: () => void; onSaved: () => void }) {
  const [instructor, setInstructor] = useState(section.instructor)
  const [rows, setRows] = useState<Row[]>(section.meetings.map((m) => ({ ...m })))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const update = (i: number, patch: Partial<Row>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))

  return (
    <Card className="space-y-3 p-4 ring-2! ring-indigo-500!">
      <div className="font-semibold">Edit section {section.number}</div>
      <p className="text-xs text-zinc-500">Now: {sectionSummary(section)}</p>
      <label className="block space-y-1">
        <span className="text-xs font-medium text-zinc-500">Instructor</span>
        <Input value={instructor} onChange={(e) => setInstructor(e.target.value)} />
      </label>
      <div className="space-y-2">
        <span className="text-xs font-medium text-zinc-500">Meetings</span>
        {rows.map((r, i) => (
          <div key={i} className="grid grid-cols-[4.5rem_1fr_1fr_1fr_auto] items-center gap-1.5">
            <Select className="h-10 px-1.5 text-sm" value={r.day} onChange={(e) => update(i, { day: Number(e.target.value) })} aria-label="Day">
              {DAY_SHORT.map((d, k) => (
                <option key={k} value={k}>
                  {d}
                </option>
              ))}
            </Select>
            <Input type="time" className="h-10 px-1.5 text-sm" value={r.start} onChange={(e) => update(i, { start: e.target.value })} aria-label="Start" />
            <Input type="time" className="h-10 px-1.5 text-sm" value={r.end} onChange={(e) => update(i, { end: e.target.value })} aria-label="End" />
            <Input className="h-10 px-2 text-sm" placeholder="Room" value={r.room} onChange={(e) => update(i, { room: e.target.value })} aria-label="Room" />
            <button className="rounded-lg p-1.5 text-zinc-400 hover:bg-zinc-100 dark:hover:bg-zinc-800" onClick={() => setRows(rows.filter((_, j) => j !== i))} aria-label="Remove meeting">
              <XIcon className="size-4" />
            </button>
          </div>
        ))}
        <button
          className="text-sm font-medium text-indigo-600 dark:text-indigo-400"
          onClick={() => setRows([...rows, { ...(rows[rows.length - 1] ?? { day: 0, start: '08:00', end: '09:30', room: '' }) }])}
        >
          + Add meeting
        </button>
      </div>
      <ErrorText>{error}</ErrorText>
      <div className="flex gap-2">
        <Button variant="ghost" className="flex-1" onClick={onCancel}>
          Cancel
        </Button>
        <Button
          className="flex-1"
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await api.put(`/admin/sections/${section.id}`, { instructor, meetings: rows })
              onSaved()
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          Save
        </Button>
      </div>
    </Card>
  )
}
