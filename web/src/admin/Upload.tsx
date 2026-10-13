import { useState } from 'react'
import { api, errorText } from '../lib/api'
import { Badge, Button, Card, Confirm, ErrorText, Field, Input, Notice, Select, cx } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface Change {
  kind: 'course' | 'section'
  code: string
  section: string | null
  detail: string
}
interface Issue {
  line: number
  message: string
}
interface Preview {
  rows: number
  courses: number
  sections: number
  errors: Issue[]
  warnings: Issue[]
  newMajors: string[]
  added: Change[]
  updated: Change[]
  removed: Change[]
  studentsAffected: number
  canApply: boolean
}

export default function Upload() {
  const { semester, semesters, setSemesterId, reloadSemesters } = useAdmin()
  const [creating, setCreating] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [text, setText] = useState('')
  const [paste, setPaste] = useState(false)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [done, setDone] = useState('')
  const [publishing, setPublishing] = useState(false)

  const form = () => {
    const f = new FormData()
    if (paste) f.append('text', text)
    else if (file) f.append('file', file)
    return f
  }

  const run = async (fn: () => Promise<void>) => {
    setBusy(true)
    setError('')
    try {
      await fn()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const reset = () => {
    setPreview(null)
    setDone('')
  }

  return (
    <>
      <PageTitle title="Upload a semester schedule" sub="One row per section meeting: course code, name, major, type (Core / LS / Elective), year, section, instructor, days, time, room." />

      <div className="grid gap-5 lg:grid-cols-[22rem_1fr]">
        <div className="space-y-5">
          <Card className="space-y-3 p-4">
            <div className="text-sm font-semibold">1. Semester</div>
            {semesters.length > 0 && !creating ? (
              <>
                <Select className="w-full" value={semester?.id ?? ''} onChange={(e) => { setSemesterId(Number(e.target.value)); reset() }}>
                  {semesters.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name} {s.isCurrent ? '(live)' : s.publishedAt ? '' : '(not published)'}
                    </option>
                  ))}
                </Select>
                <button className="text-sm font-medium text-indigo-600 dark:text-indigo-400" onClick={() => setCreating(true)}>
                  + New semester
                </button>
              </>
            ) : (
              <NewSemester
                onCancel={semesters.length > 0 ? () => setCreating(false) : undefined}
                onCreated={async (id) => {
                  await reloadSemesters()
                  setSemesterId(id)
                  setCreating(false)
                  reset()
                }}
              />
            )}
          </Card>

          {semester && !creating && (
            <Card className="space-y-3 p-4">
              <div className="flex items-center justify-between">
                <div className="text-sm font-semibold">2. Schedule file</div>
                <button className="text-xs text-indigo-600 dark:text-indigo-400" onClick={() => { setPaste(!paste); reset() }}>
                  {paste ? 'Upload a file instead' : 'Paste from Excel instead'}
                </button>
              </div>
              {paste ? (
                <textarea
                  className="h-40 w-full rounded-xl bg-white p-3 font-mono text-xs ring-1 ring-zinc-200 outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-zinc-900 dark:ring-zinc-800"
                  placeholder="Copy the rows from Excel, including the header row, and paste here"
                  value={text}
                  onChange={(e) => { setText(e.target.value); reset() }}
                />
              ) : (
                <label className="flex cursor-pointer flex-col items-center justify-center gap-1 rounded-xl border-2 border-dashed border-zinc-300 p-6 text-center text-sm hover:border-indigo-400 dark:border-zinc-700">
                  <span className="font-medium">{file ? file.name : 'Choose .xlsx or .csv'}</span>
                  <span className="text-xs text-zinc-500">{file ? `${Math.ceil(file.size / 1024)} KB · tap to change` : 'The first row must be the header'}</span>
                  <input type="file" accept=".xlsx,.csv,.tsv,.txt" className="hidden" onChange={(e) => { setFile(e.target.files?.[0] ?? null); reset() }} />
                </label>
              )}
              <Button
                className="w-full"
                variant="secondary"
                busy={busy && !preview}
                disabled={paste ? !text.trim() : !file}
                onClick={() => run(async () => { setDone(''); setPreview(await api.form<Preview>(`/admin/semesters/${semester.id}/upload/preview`, form())) })}
              >
                Preview changes
              </Button>
            </Card>
          )}

          {semester && !creating && (
            <Card className="space-y-2 p-4">
              <div className="text-sm font-semibold">3. Publish</div>
              {semester.isCurrent ? (
                <p className="text-sm text-zinc-500">{semester.name} is live. Uploads update it right away and students see a “Changed” badge on edited classes.</p>
              ) : (
                <>
                  <p className="text-sm text-zinc-500">
                    {semester.name} isn't visible to students. Publishing it makes every student redo setup for {semester.name}, starting from their major.
                  </p>
                  <Button className="w-full" disabled={!semester.courses} onClick={() => setPublishing(true)}>
                    Publish {semester.name}
                  </Button>
                </>
              )}
            </Card>
          )}
        </div>

        <div className="min-w-0 space-y-4">
          <ErrorText>{error}</ErrorText>
          {done && <Notice tone="ok">{done}</Notice>}
          {preview && semester && (
            <PreviewView
              p={preview}
              live={semester.isCurrent}
              busy={busy}
              onApply={() =>
                run(async () => {
                  const r = await api.form<{ added: number; updated: number; removed: number }>(`/admin/semesters/${semester.id}/upload/apply`, form())
                  setPreview(null)
                  setDone(`Saved: ${r.added} added, ${r.updated} updated, ${r.removed} removed.${semester.isCurrent ? '' : ' Publish when you are ready.'}`)
                  await reloadSemesters()
                })
              }
            />
          )}
          {!preview && !done && (
            <Card className="p-5 text-sm text-zinc-500">
              <p className="mb-2 font-medium text-zinc-700 dark:text-zinc-300">Example</p>
              <pre className="overflow-x-auto text-xs leading-relaxed">{`Course Code,Course Name,Major,Type,Year,Credits,Section,Instructor,Days,Time,Room
CS201,Data Structures,CS,Core,2,4,1,Dr. Laura Becker,Sun/Tue,08:00-09:30,B105
CS201,Data Structures,CS,Core,2,4,1,Dr. Laura Becker,Thu,09:45-11:15,Lab L1
LS101,Academic English,,LS,,3,2,Ms. Aisha Al-Balushi,Mon Wed,1:30 PM - 3:00 PM,A203
CS360,Mobile App Development,CS;LOG,Elective,,3,1,Dr. Omar Al-Kindi,UTR,15:15-16:45,C012`}</pre>
              <ul className="mt-3 list-disc space-y-1 pl-5">
                <li>A section that meets at different times takes one row per time.</li>
                <li>Several majors: separate with ; or /. Electives with no major are open to everyone.</li>
                <li>Re-uploading only changes the rows that differ; students keep their picks.</li>
              </ul>
            </Card>
          )}
        </div>
      </div>

      {semester && (
        <Confirm
          open={publishing}
          onClose={() => setPublishing(false)}
          title={`Publish ${semester.name}?`}
          body={<>Students will see {semester.name} at their next visit and redo their setup, starting from their major. Their last picks are shown as suggestions.</>}
          confirmLabel="Publish"
          onConfirm={async () => {
            await api.post(`/admin/semesters/${semester.id}/publish`)
            await reloadSemesters()
            setDone(`${semester.name} is live.`)
          }}
        />
      )}
    </>
  )
}

function NewSemester({ onCreated, onCancel }: { onCreated: (id: number) => void; onCancel?: () => void }) {
  const now = new Date()
  const [term, setTerm] = useState(now.getMonth() >= 7 ? 'Spring' : 'Fall')
  const [year, setYear] = useState(now.getMonth() >= 7 ? now.getFullYear() + 1 : now.getFullYear())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-2">
        <Field label="Term">
          <Select className="w-full" value={term} onChange={(e) => setTerm(e.target.value)}>
            <option>Fall</option>
            <option>Spring</option>
            <option>Summer</option>
          </Select>
        </Field>
        <Field label="Year">
          <Input type="number" value={year} onChange={(e) => setYear(Number(e.target.value))} />
        </Field>
      </div>
      <ErrorText>{error}</ErrorText>
      <div className="flex gap-2">
        {onCancel && (
          <Button variant="ghost" className="flex-1" onClick={onCancel}>
            Cancel
          </Button>
        )}
        <Button
          className="flex-1"
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              const s = await api.post<{ id: number }>('/admin/semesters', { term, year })
              onCreated(s.id)
            } catch (e) {
              setError(errorText(e))
            } finally {
              setBusy(false)
            }
          }}
        >
          Create {term} {year}
        </Button>
      </div>
    </div>
  )
}

function PreviewView({ p, live, busy, onApply }: { p: Preview; live: boolean; busy: boolean; onApply: () => void }) {
  const nothing = p.added.length + p.updated.length + p.removed.length === 0
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-3 gap-3">
        {[
          ['Rows', p.rows],
          ['Courses', p.courses],
          ['Sections', p.sections],
        ].map(([l, v]) => (
          <Card key={l} className="p-3">
            <div className="text-xs text-zinc-500">{l}</div>
            <div className="text-xl font-semibold tabular-nums">{v}</div>
          </Card>
        ))}
      </div>

      {p.errors.length > 0 && (
        <IssueList title={`${p.errors.length} row${p.errors.length === 1 ? '' : 's'} to fix`} tone="red" items={p.errors} />
      )}
      {p.newMajors.length > 0 && <Notice>New majors will be added: {p.newMajors.join(', ')}. You can rename them under Majors.</Notice>}
      {live && p.studentsAffected > 0 && (
        <Notice tone="warn">
          {p.studentsAffected} student{p.studentsAffected === 1 ? '' : 's'} picked a section that changes or is removed. Changed classes get a badge; students whose section is removed are asked to re-pick.
        </Notice>
      )}

      <ChangeList title="Added" tone="green" items={p.added} />
      <ChangeList title="Changed" tone="amber" items={p.updated} />
      <ChangeList title="Removed" tone="red" items={p.removed} />
      {nothing && p.errors.length === 0 && <Notice tone="ok">Nothing changed: the semester already matches this file.</Notice>}
      {p.warnings.length > 0 && <IssueList title={`${p.warnings.length} warning${p.warnings.length === 1 ? '' : 's'}`} tone="amber" items={p.warnings} collapsed />}

      <div className="sticky bottom-0 -mx-4 border-t border-zinc-200 bg-zinc-50/90 px-4 py-3 backdrop-blur dark:border-zinc-800 dark:bg-zinc-950/90">
        <Button className="w-full" busy={busy} disabled={!p.canApply || nothing} onClick={onApply}>
          {p.canApply ? (nothing ? 'Nothing to save' : 'Confirm and save') : 'Fix the problems first'}
        </Button>
      </div>
    </div>
  )
}

function ChangeList({ title, tone, items }: { title: string; tone: 'green' | 'amber' | 'red'; items: Change[] }) {
  if (items.length === 0) return null
  return (
    <Card className="overflow-hidden">
      <div className="flex items-center gap-2 border-b border-zinc-100 px-4 py-2.5 dark:border-zinc-800">
        <Badge tone={tone}>{title}</Badge>
        <span className="text-sm text-zinc-500">{items.length}</span>
      </div>
      <ul className="max-h-80 divide-y divide-zinc-100 overflow-y-auto text-sm dark:divide-zinc-800">
        {items.map((c, i) => (
          <li key={i} className="px-4 py-2">
            <span className="font-medium">
              {c.code}
              {c.section && `-${c.section}`}
            </span>
            <span className="ml-1.5 text-xs text-zinc-400">{c.kind}</span>
            <div className="text-zinc-500">{c.detail}</div>
          </li>
        ))}
      </ul>
    </Card>
  )
}

function IssueList({ title, tone, items, collapsed }: { title: string; tone: 'red' | 'amber'; items: Issue[]; collapsed?: boolean }) {
  const [open, setOpen] = useState(!collapsed)
  return (
    <Card className={cx('overflow-hidden', tone === 'red' && 'ring-red-200! dark:ring-red-900!')}>
      <button className="flex w-full items-center gap-2 px-4 py-2.5 text-left" onClick={() => setOpen(!open)}>
        <Badge tone={tone}>{title}</Badge>
        <span className="ml-auto text-xs text-zinc-400">{open ? 'Hide' : 'Show'}</span>
      </button>
      {open && (
        <ul className="max-h-72 divide-y divide-zinc-100 overflow-y-auto border-t border-zinc-100 text-sm dark:divide-zinc-800 dark:border-zinc-800">
          {items.map((x, i) => (
            <li key={i} className="flex gap-3 px-4 py-2">
              <span className="w-14 shrink-0 text-zinc-400 tabular-nums">Row {x.line}</span>
              <span>{x.message}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}
