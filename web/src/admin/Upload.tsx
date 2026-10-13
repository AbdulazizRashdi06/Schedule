import { useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Semester } from '../lib/types'
import { Button, Confirm, ErrorText, Notice, Select, TextField, cx } from '../components/ui'
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

const LABELS = ['Semester', 'File', 'Check', 'Publish']
type Tab = 'added' | 'changed' | 'removed' | 'errors' | 'majors'

export default function Upload() {
  const { semester, semesters, setSemesterId, reloadSemesters } = useAdmin()
  const [step, setStep] = useState(0)
  // Step 1: an existing semester, or a new one.
  const [pick, setPick] = useState<number | 'new' | null>(semester?.id ?? (semesters.length ? null : 'new'))
  // Step 2: a file, or rows pasted from Excel.
  const [file, setFile] = useState<File | null>(null)
  const [text, setText] = useState('')
  const [paste, setPaste] = useState(false)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [checking, setChecking] = useState(false)
  // Step 4: publish only, without a new file.
  const [publishOnly, setPublishOnly] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [done, setDone] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const target = semesters.find((s) => s.id === (pick === 'new' ? -1 : pick)) ?? null

  const form = () => {
    const f = new FormData()
    if (paste) f.append('text', text)
    else if (file) f.append('file', file)
    return f
  }

  const check = async (s: Semester, f: FormData) => {
    setChecking(true)
    setError('')
    setPreview(null)
    try {
      setPreview(await api.form<Preview>(`/admin/semesters/${s.id}/upload/preview`, f))
    } catch (e) {
      setError(errorText(e))
    } finally {
      setChecking(false)
    }
  }

  const go = (n: number) => {
    setError('')
    setStep(n)
  }

  return (
    <>
      <PageTitle title="Upload a timetable" />
      <ol aria-label="Progress" className="grid grid-cols-4 gap-2.5">
        {LABELS.map((l, i) => (
          <li key={l} aria-current={i === step ? 'step' : undefined} className="flex flex-col gap-2">
            <span className="h-1.5 overflow-hidden rounded-[3px] bg-navy/[0.12]">
              <span className="fill block h-full bg-navy" style={{ transform: `scaleX(${i <= step ? 1 : 0})` }} />
            </span>
            <span className={cx('text-[15px]', i === step ? 'font-extrabold' : 'font-semibold', i > step && 'text-muted')}>{l}</span>
          </li>
        ))}
      </ol>

      {step === 0 && (
        <Pane>
          <h2 className="text-[26px] font-extrabold">Which semester is this file for?</h2>
          <div role="radiogroup" aria-label="Semester" className="flex flex-col gap-2">
            {semesters.map((s) => (
              <Choice key={s.id} on={pick === s.id} onClick={() => setPick(s.id)} name={s.name} meta={s.isCurrent ? 'Published' : s.publishedAt ? 'Not live' : 'Draft'} />
            ))}
            <Choice on={pick === 'new'} onClick={() => setPick('new')} name="New semester" meta="Create" />
          </div>
          {target && !target.isCurrent && (
            <div>
              <Button variant="danger" size="sm" onClick={() => setDeleting(true)}>
                Delete {target.name}
              </Button>
            </div>
          )}
          {target && (
            <Confirm
              open={deleting}
              title={`Delete ${target.name}?`}
              body="This removes the semester with its courses, sections, students’ picks and defaults. It can’t be undone."
              confirmLabel="Delete"
              danger
              onClose={() => setDeleting(false)}
              onConfirm={async () => {
                await api.del(`/admin/semesters/${target.id}`)
                setPick(null)
                await reloadSemesters()
              }}
            />
          )}
          {pick === 'new' ? (
            <NewSemester
              onCreated={async (id) => {
                await reloadSemesters()
                setSemesterId(id)
                setPick(id)
                go(1)
              }}
            />
          ) : (
            <div>
              <Button
                disabled={!target}
                onClick={() => {
                  if (!target) return
                  setSemesterId(target.id)
                  setPreview(null)
                  setPublishOnly(false)
                  go(1)
                }}
              >
                Continue
              </Button>
            </div>
          )}
        </Pane>
      )}

      {step === 1 && target && (
        <Pane>
          <div className="flex flex-wrap items-baseline justify-between gap-3">
            <h2 className="text-[26px] font-extrabold">Add the file for {target.name}</h2>
            <button
              type="button"
              className="label text-[11px] link-hover"
              onClick={() => {
                setPaste(!paste)
                setPreview(null)
              }}
            >
              {paste ? 'Upload a file instead' : 'Paste from Excel instead'}
            </button>
          </div>
          {paste ? (
            <div className="flex flex-col gap-2">
              <label htmlFor="paste" className="label text-[11px] tracking-[0.12em]">
                Rows from Excel, header first
              </label>
              <textarea
                id="paste"
                className="field h-44 rounded-[20px] bg-paper p-4 font-mono text-[13px] outline-none"
                value={text}
                onChange={(e) => {
                  setText(e.target.value)
                  setPreview(null)
                }}
              />
              <div>
                <Button variant="secondary" size="sm" disabled={!text.trim() || checking} onClick={() => check(target, form())}>
                  {checking ? 'Reading…' : 'Read the rows'}
                </Button>
              </div>
            </div>
          ) : (
            <DropZone
              file={file}
              onFile={(f) => {
                setFile(f)
                const data = new FormData()
                data.append('file', f)
                check(target, data)
              }}
            />
          )}
          {(file || paste) && (checking || preview) && (
            <div className="pane flex flex-col gap-3 rounded-[20px] bg-paper px-5 py-[18px] shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]">
              <div className="flex items-baseline justify-between gap-3">
                <span className="text-[17px] font-extrabold [overflow-wrap:anywhere]">{paste ? 'Pasted rows' : file?.name}</span>
                <span className="label shrink-0 text-[12px] tracking-normal" aria-live="polite">
                  {checking ? 'Checking…' : preview && `${file && !paste ? `${Math.ceil(file.size / 1024)} KB, ` : ''}${preview.rows} rows`}
                </span>
              </div>
              <span className="h-1.5 overflow-hidden rounded-[3px] bg-navy/[0.12]">
                <span className={cx('fill block h-full bg-navy', checking && 'origin-left animate-[grow_1200ms_linear_forwards]')} style={checking ? undefined : { transform: 'scaleX(1)' }} />
              </span>
            </div>
          )}
          <ErrorText>{error}</ErrorText>
          <div className="flex flex-wrap items-center gap-2.5">
            <Button variant="secondary" onClick={() => go(0)}>
              Back
            </Button>
            <Button disabled={!preview || checking} onClick={() => go(2)}>
              Check the file
            </Button>
            {!target.isCurrent && (target.courses ?? 0) > 0 && (
              <button
                type="button"
                className="label ml-1 text-[11px] link-hover"
                onClick={() => {
                  setPublishOnly(true)
                  go(3)
                }}
              >
                Publish {target.name} as it is
              </button>
            )}
          </div>
        </Pane>
      )}

      {step === 2 && target && preview && <CheckStep p={preview} onBack={() => go(1)} onNext={() => go(3)} />}

      {step === 3 && target && (
        <Pane>
          {done ? (
            <div className="pane flex flex-col gap-3.5">
              <h2 className="display text-[72px] leading-[0.86] tracking-[-0.05em]">{done === 'draft' ? 'Saved.' : 'Published.'}</h2>
              <p className="text-[17px] font-semibold">
                {done === 'draft' ? `${target.name} is saved as a draft. Students can’t see it until you publish it.` : `${target.name} is live. Students see it next time they open the app.`}
              </p>
              <Link to="/admin" className="press flex h-[52px] items-center self-start rounded-full bg-navy px-6 text-[17px] font-extrabold text-cream">
                Back to home
              </Link>
            </div>
          ) : (
            <>
              <h2 className="text-[40px] leading-none font-extrabold tracking-[-0.03em]">{target.isCurrent ? `Ready to update ${target.name}.` : `Ready to publish ${target.name}.`}</h2>
              <p className="text-[17px] leading-[1.45] font-semibold">
                {target.isCurrent
                  ? preview && preview.studentsAffected > 0
                    ? `${preview.studentsAffected} ${preview.studentsAffected === 1 ? 'student gets' : 'students get'} the new times right away. Changed classes get a badge, and anyone who loses a section is asked to pick again the next time they open the app.`
                    : 'Students get the new times right away. Nobody’s picks are affected.'
                  : `Every student sets up ${target.name} again, starting from their major. Their last picks show as suggestions.`}
              </p>
              <ErrorText>{error}</ErrorText>
              <div className="flex flex-wrap gap-2.5">
                <Button variant="secondary" className="h-[52px]" onClick={() => go(publishOnly ? 1 : 2)}>
                  Back
                </Button>
                {!target.isCurrent && !publishOnly && (
                  <Button
                    variant="secondary"
                    className="h-[52px]"
                    busy={busy}
                    onClick={async () => {
                      setBusy(true)
                      setError('')
                      try {
                        await api.form(`/admin/semesters/${target.id}/upload/apply`, form())
                        await reloadSemesters()
                        setDone('draft')
                      } catch (e) {
                        setError(errorText(e))
                      } finally {
                        setBusy(false)
                      }
                    }}
                  >
                    Save as draft
                  </Button>
                )}
                <Button
                  className="h-[52px] px-7 text-[17px]"
                  busy={busy}
                  onClick={async () => {
                    setBusy(true)
                    setError('')
                    try {
                      if (!publishOnly) await api.form(`/admin/semesters/${target.id}/upload/apply`, form())
                      if (!target.isCurrent) await api.post(`/admin/semesters/${target.id}/publish`)
                      await reloadSemesters()
                      setDone('live')
                    } catch (e) {
                      setError(errorText(e))
                    } finally {
                      setBusy(false)
                    }
                  }}
                >
                  {busy ? 'Publishing…' : 'Publish'}
                </Button>
              </div>
            </>
          )}
        </Pane>
      )}
    </>
  )
}

function Pane({ children }: { children: ReactNode }) {
  return <section className="pane flex max-w-[640px] flex-col gap-4">{children}</section>
}

function Choice({ on, onClick, name, meta }: { on: boolean; onClick: () => void; name: string; meta: string }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={on}
      onClick={onClick}
      className={cx('press flex h-[60px] items-center justify-between rounded-[18px] px-[18px] text-left', on ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
    >
      <span className="text-[17px] font-extrabold">{name}</span>
      <span className="label text-[11px]">{meta}</span>
    </button>
  )
}

function DropZone({ file, onFile }: { file: File | null; onFile: (f: File) => void }) {
  const input = useRef<HTMLInputElement>(null)
  const [over, setOver] = useState(false)
  return (
    <div
      onDragOver={(e) => {
        e.preventDefault()
        setOver(true)
      }}
      onDragLeave={() => setOver(false)}
      onDrop={(e) => {
        e.preventDefault()
        setOver(false)
        const f = e.dataTransfer.files[0]
        if (f) onFile(f)
      }}
      className={cx('flex flex-col items-center gap-3 rounded-[24px] border-[2.5px] border-dashed px-6 py-11 text-center transition-colors duration-150', over ? 'border-navy bg-ice/50' : 'border-navy/30')}
    >
      <span className="text-[20px] font-extrabold">{file ? 'Drop another file to replace it' : 'Drop an .xlsx or .csv here'}</span>
      <span className="text-[14px] font-semibold text-muted">One row per section meeting: code, name, major, type, year, section, instructor, days, time, room.</span>
      <Button onClick={() => input.current?.click()}>Choose file</Button>
      <input
        ref={input}
        type="file"
        accept=".xlsx,.csv,.tsv,.txt"
        className="sr"
        tabIndex={-1}
        onChange={(e) => {
          const f = e.target.files?.[0]
          if (f) onFile(f)
          e.target.value = ''
        }}
      />
    </div>
  )
}

function CheckStep({ p, onBack, onNext }: { p: Preview; onBack: () => void; onNext: () => void }) {
  const [tab, setTab] = useState<Tab>(p.errors.length ? 'errors' : p.updated.length ? 'changed' : 'added')
  const changeRows = (list: Change[]) => list.map((c) => ({ a: `${c.code}${c.section ? ` ${c.section}` : ''}`, b: c.kind === 'course' ? 'Course' : 'Section', c: c.detail }))
  const DATA: Record<Tab, { n: number; label: string; rows: { a: string; b: string; c: string; bad?: boolean }[] }> = {
    added: { n: p.added.length, label: 'Added', rows: changeRows(p.added) },
    changed: { n: p.updated.length, label: 'Changed', rows: changeRows(p.updated) },
    removed: { n: p.removed.length, label: 'Removed', rows: changeRows(p.removed) },
    errors: {
      n: p.errors.length,
      label: 'Errors',
      rows: [...p.errors.map((e) => ({ a: `Row ${e.line}`, b: 'Error', c: e.message, bad: true })), ...p.warnings.map((e) => ({ a: `Row ${e.line}`, b: 'Warning', c: e.message }))],
    },
    majors: { n: p.newMajors.length, label: 'New majors', rows: p.newMajors.map((m) => ({ a: 'New', b: m, c: 'Will be created. Set its name and electives in Majors.' })) },
  }
  const nothing = p.added.length + p.updated.length + p.removed.length === 0
  const keys = Object.keys(DATA) as Tab[]
  return (
    <section className="pane flex flex-col gap-[18px]">
      <div role="tablist" aria-label="Changes" className="grid grid-cols-[repeat(auto-fit,minmax(120px,1fr))] gap-2">
        {keys.map((k) => {
          const on = k === tab
          const bad = k === 'errors'
          return (
            <button
              key={k}
              type="button"
              role="tab"
              id={`tab-${k}`}
              aria-selected={on}
              aria-controls="tab-panel"
              onClick={() => setTab(k)}
              className={cx(
                'press flex min-h-[84px] flex-col items-start justify-between rounded-[18px] px-3.5 py-3 text-left',
                on ? (bad ? 'bg-alert-ink text-cream' : 'bg-navy text-cream') : bad ? 'bg-alert-bg text-alert-ink' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.14)]',
              )}
            >
              <span className="text-[34px] leading-none font-extrabold tracking-[-0.03em] tabular-nums">{DATA[k].n}</span>
              <span className="text-[14px] font-bold">{DATA[k].label}</span>
            </button>
          )
        })}
      </div>
      <div id="tab-panel" role="tabpanel" aria-labelledby={`tab-${tab}`} className="flex max-h-[420px] flex-col overflow-y-auto border-t-[2.5px] border-navy">
        {DATA[tab].rows.length === 0 && <p className="px-1 py-3 text-[15px] font-semibold text-muted">Nothing here.</p>}
        {DATA[tab].rows.map((r, i) => (
          <div key={i} className="grid grid-cols-[90px_minmax(0,1fr)_minmax(0,1.4fr)] items-baseline gap-4 border-b-[1.5px] border-navy/10 px-1 py-3">
            <span className="label text-[12px] tracking-normal">{r.a}</span>
            <span className="text-[15px] font-extrabold">{r.b}</span>
            <span className={cx('text-[15px] font-semibold', r.bad && 'text-alert')}>{r.c}</span>
          </div>
        ))}
      </div>
      {nothing && p.errors.length === 0 && <Notice>Nothing changed: the semester already matches this file.</Notice>}
      <div className="flex flex-wrap items-center gap-2.5">
        <Button variant="secondary" onClick={onBack}>
          Back
        </Button>
        <Button disabled={!p.canApply || nothing} onClick={onNext}>
          {!p.canApply ? `Fix ${p.errors.length} bad ${p.errors.length === 1 ? 'row' : 'rows'} first` : nothing ? 'Nothing to save' : 'Confirm'}
        </Button>
        {!p.canApply && <span className="text-[14px] font-semibold text-muted">Fix them in the file and upload it again.</span>}
      </div>
    </section>
  )
}

function NewSemester({ onCreated }: { onCreated: (id: number) => void }) {
  const now = new Date()
  const [term, setTerm] = useState(now.getMonth() >= 7 ? 'Spring' : 'Fall')
  const [year, setYear] = useState(String(now.getMonth() >= 7 ? now.getFullYear() + 1 : now.getFullYear()))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <div className="pane flex flex-col gap-3">
      <div className="flex flex-wrap items-end gap-2.5">
        <label className="flex flex-col gap-1.5">
          <span className="label text-[11px] tracking-[0.12em]">Term</span>
          <Select value={term} onChange={(e) => setTerm(e.target.value)} className="w-40">
            <option>Fall</option>
            <option>Spring</option>
            <option>Summer</option>
          </Select>
        </label>
        <TextField label="Year" inputMode="numeric" value={year} onChange={(e) => setYear(e.target.value.replace(/\D/g, '').slice(0, 4))} className="w-32" />
      </div>
      <ErrorText>{error}</ErrorText>
      <div>
        <Button
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              const s = await api.post<{ id: number }>('/admin/semesters', { term, year: Number(year) })
              onCreated(s.id)
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          {busy ? 'Creating…' : `Create ${term} ${year} and continue`}
        </Button>
      </div>
    </div>
  )
}
