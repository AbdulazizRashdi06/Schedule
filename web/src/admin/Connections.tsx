import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Button, Checkbox, ErrorText, TextField } from '../components/ui'
import { PageTitle, shortDate, useAdmin } from './AdminLayout'

type Kind = 'timetable' | 'student-courses'

interface Connection {
  kind: Kind
  url: string
  headerName: string
  hasSecret: boolean
  listPath: string
  fields: Record<string, string>
  enabled: boolean
  autoDaily: boolean
  lastRunAt: string | null
  lastResult: string | null
}

const FIELDS: Record<Kind, { key: string; label: string; hint: string }[]> = {
  timetable: [
    { key: 'code', label: 'Course code', hint: 'courseCode' },
    { key: 'name', label: 'Course name', hint: 'title' },
    { key: 'section', label: 'Section', hint: 'section.number' },
    { key: 'instructor', label: 'Instructor', hint: 'teacher.name' },
    { key: 'date', label: 'Class date', hint: 'date' },
    { key: 'days', label: 'Weekdays (weekly classes)', hint: 'days' },
    { key: 'start', label: 'Start time', hint: 'start' },
    { key: 'end', label: 'End time', hint: 'end' },
    { key: 'time', label: 'Time range (instead of start and end)', hint: 'time' },
    { key: 'room', label: 'Room', hint: 'room' },
  ],
  'student-courses': [
    { key: 'code', label: 'Course code', hint: 'courseCode' },
    { key: 'section', label: 'Section', hint: 'section' },
  ],
}

const TEXT: Record<Kind, { title: string; sub: string; url: string }> = {
  timetable: {
    title: 'Timetable API',
    sub: 'Classy reads every class from this address. The result waits under Upload → From EduWave until someone applies it.',
    url: 'https://api.example.edu/timetable?term={semester}',
  },
  'student-courses': {
    title: 'Student courses API',
    sub: 'Classy asks this address for one student’s courses. Students then get a “Get my courses from GUtech” button.',
    url: 'https://api.example.edu/students/{studentId}/courses',
  },
}

/** Owners connect any JSON API: where it is, how to sign in to it, and which of its fields are which. */
export default function Connections() {
  const [list, setList] = useState<Connection[] | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    api.get<Connection[]>('/admin/connections').then(setList, (e) => setError(errorText(e)))
  }, [])

  return (
    <>
      <PageTitle
        title="Connections."
        sub="Connect GUtech’s systems to Classy. Any JSON API works: give its address, then say which of its fields hold what."
      />
      <ErrorText>{error}</ErrorText>
      {list?.map((c) => <ConnectionCard key={c.kind} initial={c} />)}
      <section className="flex max-w-[760px] flex-col gap-2 text-[15px] font-semibold text-muted">
        <h2 className="text-[20px] font-extrabold text-navy">GUtech can also send data to Classy</h2>
        <p>
          Instead of Classy fetching, their system can send the timetable to <code className="font-mono">POST /api/sync/timetable</code> and students’ courses to{' '}
          <code className="font-mono">POST /api/sync/enrollments</code>, with a token from Accounts → Classy Sync. The API documentation has the details.
        </p>
      </section>
    </>
  )
}

function ConnectionCard({ initial }: { initial: Connection }) {
  const { semester } = useAdmin()
  const kind = initial.kind
  const [c, setC] = useState(initial)
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [note, setNote] = useState('')
  const [studentId, setStudentId] = useState('')
  const [studentResult, setStudentResult] = useState<{ code: string; section: string; inClassy: boolean }[] | null>(null)

  const set = (patch: Partial<Connection>) => setC({ ...c, ...patch })
  const setField = (key: string, value: string) => set({ fields: { ...c.fields, [key]: value } })

  const run = async (label: string, fn: () => Promise<void>) => {
    setBusy(label)
    setError('')
    setNote('')
    try {
      await fn()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy('')
    }
  }
  const save = (patch: Partial<Connection> = {}) =>
    run('save', async () => {
      const next = { ...c, ...patch }
      const saved = await api.put<Connection>(`/admin/connections/${kind}`, {
        url: next.url,
        headerName: next.headerName,
        headerValue: secret === '' ? null : secret,
        listPath: next.listPath,
        fields: next.fields,
        enabled: next.enabled,
        autoDaily: next.autoDaily,
      })
      setC(saved)
      setSecret('')
      setNote('Saved.')
    })

  return (
    <section className="flex max-w-[760px] flex-col gap-4 rounded-[20px] p-5 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
      <div className="flex flex-col gap-1">
        <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">{TEXT[kind].title}</h2>
        <p className="text-[15px] font-semibold text-muted">{TEXT[kind].sub}</p>
      </div>

      <TextField label="Address" mono value={c.url} placeholder={TEXT[kind].url} onChange={(e) => set({ url: e.target.value })} />
      <p className="-mt-2 text-[13px] font-semibold text-muted">
        {kind === 'student-courses' ? '{studentId} becomes the student’s ID, like 24-0161. ' : ''}
        {'{semester}'} becomes the semester’s name, like Fall 2026.
      </p>
      <div className="grid gap-3 sm:grid-cols-2">
        <TextField label="Sign-in header (optional)" mono value={c.headerName} placeholder="X-Api-Key" onChange={(e) => set({ headerName: e.target.value })} />
        <TextField
          label="Header value"
          type="password"
          autoComplete="off"
          value={secret}
          placeholder={c.hasSecret ? 'Saved. Type to replace it' : 'Bearer … or the key'}
          onChange={(e) => setSecret(e.target.value)}
        />
      </div>
      <TextField label="Where the list is in the reply" mono value={c.listPath} placeholder="data.items  (empty when the reply is the list)" onChange={(e) => set({ listPath: e.target.value })} />
      <p className="-mt-2 text-[13px] font-semibold text-muted">
        Use dots to go inside, and * to go into every item of a list: <span className="font-mono">courses.*.sections.*.classes</span>. A field missing on an item is taken from the item around it, so a class
        can use its course’s code.
      </p>

      <div className="flex flex-col gap-2">
        <h3 className="text-[16px] font-extrabold">Which field is which</h3>
        <div className="grid gap-x-3 gap-y-2 sm:grid-cols-2">
          {FIELDS[kind].map((f) => (
            <TextField key={f.key} label={f.label} mono value={c.fields[f.key] ?? ''} placeholder={f.hint} onChange={(e) => setField(f.key, e.target.value)} />
          ))}
        </div>
      </div>

      <div className="flex flex-col gap-2.5">
        <Checkbox checked={c.enabled} onChange={(v) => set({ enabled: v })} label={kind === 'timetable' ? 'Turned on' : 'Turned on: students see “Get my courses from GUtech”'} />
        {kind === 'timetable' && <Checkbox checked={c.autoDaily} onChange={(v) => set({ autoDaily: v })} label="Fetch it every morning at 06:00" />}
      </div>

      <ErrorText>{error}</ErrorText>
      {note && (
        <p role="status" className="text-[15px] font-bold">
          {note}
        </p>
      )}
      <div className="flex flex-wrap gap-2.5">
        <Button busy={busy === 'save'} onClick={() => save()}>
          Save
        </Button>
        {kind === 'timetable' && (
          <>
            <Button
              variant="secondary"
              busy={busy === 'test'}
              disabled={!initial.url && !c.url}
              onClick={() =>
                run('test', async () => {
                  const r = await api.post<{ count: number; courses: number; noCode: number; noSection: number; noWhen: number }>('/admin/connections/timetable/test', {
                    semesterId: semester?.id,
                  })
                  const gaps = [r.noCode && `${r.noCode} without a code`, r.noSection && `${r.noSection} without a section`, r.noWhen && `${r.noWhen} without a date or weekday`].filter(Boolean)
                  setNote(`Read ${r.count.toLocaleString()} classes in ${r.courses} courses.${gaps.length ? ` ${gaps.join(', ')}: check the field names.` : ' Every class has what Classy needs.'}`)
                })
              }
            >
              Test saved settings
            </Button>
            <Button
              variant="secondary"
              busy={busy === 'fetch'}
              onClick={() =>
                run('fetch', async () => {
                  const r = await api.post<{ courses: number; classes: number; added: number; updated: number; removed: number }>('/admin/connections/timetable/run', {
                    semesterId: semester?.id,
                  })
                  setNote(`Fetched ${r.courses} courses, ${r.classes.toLocaleString()} classes: ${r.added} added, ${r.updated} changed, ${r.removed} removed. Check and apply it under Upload.`)
                })
              }
            >
              Fetch now
            </Button>
          </>
        )}
      </div>
      {kind === 'timetable' && c.lastRunAt && (
        <p className="text-[14px] font-semibold text-muted">
          Last fetch {shortDate(c.lastRunAt)}: {c.lastResult} {c.lastResult?.includes('Waiting') && <Link to="/admin/upload" className="link-hover font-bold text-navy">Go to Upload</Link>}
        </p>
      )}

      {kind === 'student-courses' && (
        <div className="flex flex-col gap-2 border-t-[1.5px] border-navy/10 pt-4">
          <h3 className="text-[16px] font-extrabold">Try it with a student ID</h3>
          <div className="flex flex-wrap items-end gap-2.5">
            <TextField label="University ID" hideLabel mono value={studentId} placeholder="24-0161" onChange={(e) => setStudentId(e.target.value)} className="w-[180px]" />
            <Button
              variant="secondary"
              busy={busy === 'student'}
              onClick={() =>
                run('student', async () => {
                  setStudentResult(await api.post('/admin/connections/student-courses/test', { universityId: studentId }))
                })
              }
            >
              Test saved settings
            </Button>
          </div>
          {studentResult && (
            <ul className="flex flex-col gap-1 text-[15px] font-semibold">
              {studentResult.length === 0 && <li className="text-muted">No courses came back.</li>}
              {studentResult.map((r, i) => (
                <li key={i}>
                  <span className="font-mono font-bold">{r.code}</span> section {r.section || '?'} {r.inClassy ? '' : <span className="text-alert">· not in Classy this semester</span>}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}
