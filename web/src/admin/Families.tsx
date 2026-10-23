import { useEffect, useMemo, useState, type DragEvent } from 'react'
import { ApiError, api, errorText } from '../lib/api'
import { yearName } from '../lib/year'
import { Button, ErrorText, Icon, Loading, Notice, TextField, cx } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface MajorRow {
  id: number
  code: string
  name: string
  family: string | null
}

interface CourseRow {
  id: number
  code: string
  name: string
  type: string
  majors: string[]
}

interface DefaultRow {
  id: string
  major: string
  year: number
}

const YEARS = [0, 1, 2, 3, 4, 5]
/** One target's defaults: year → course codes. */
type Board = Record<number, string[]>
const norm = (code: string) => code.replace(/\s+/g, '').toUpperCase()
/** A lab (CS 1 102-L) follows its course: it isn't placed on its own, and the server copies the course's rows onto it. */
const isLab = (code: string) => norm(code).endsWith('-L')

/** A family's default courses by year, edited by dragging courses into year columns (or tap a course, then a column). */
export default function Families() {
  const { semester } = useAdmin()
  const [majors, setMajors] = useState<MajorRow[] | null>(null)
  const [courses, setCourses] = useState<CourseRow[] | null>(null)
  /** Every default of the semester as last saved. */
  const [saved, setSaved] = useState<DefaultRow[] | null>(null)
  const [family, setFamily] = useState<string | null>(null)
  /** The family code (shared defaults) or one of its majors (that major's own). */
  const [target, setTarget] = useState<string | null>(null)
  const [boards, setBoards] = useState<Record<string, Board>>({})
  const [dirty, setDirty] = useState(false)
  const [q, setQ] = useState('')
  const [onlyFamily, setOnlyFamily] = useState(true)
  const [picked, setPicked] = useState<string | null>(null)
  const [over, setOver] = useState<number | 'pool' | null>(null)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')
  const [problems, setProblems] = useState<string[]>([])

  useEffect(() => {
    api.get<MajorRow[]>('/admin/majors').then(setMajors, (e) => setError(errorText(e)))
  }, [])
  useEffect(() => {
    if (!semester) return
    setCourses(null)
    setSaved(null)
    api.get<CourseRow[]>(`/admin/semesters/${semester.id}/courses`).then(setCourses, (e) => setError(errorText(e)))
    api.get<DefaultRow[]>(`/admin/semesters/${semester.id}/defaults`).then(setSaved, (e) => setError(errorText(e)))
  }, [semester])

  const families = useMemo(() => [...new Set((majors ?? []).flatMap((m) => (m.family ? [m.family] : [])))].sort(), [majors])
  const members = useMemo(() => (majors ?? []).filter((m) => m.family === family), [majors, family])
  const byCode = useMemo(() => new Map((courses ?? []).map((c) => [norm(c.code), c])), [courses])
  /** Course code → its lab, when it has one. */
  const labOf = useMemo(() => new Map((courses ?? []).filter((c) => isLab(c.code)).map((c) => [norm(c.code).slice(0, -2), c])), [courses])

  // Pick the first family once everything has loaded.
  useEffect(() => {
    if (!family && families.length) setFamily(families[0])
  }, [families, family])

  // Load the family's boards from what's saved.
  useEffect(() => {
    if (!family || !saved) return
    const next: Record<string, Board> = {}
    for (const t of [family, ...members.map((m) => m.code)]) next[t] = {}
    for (const r of saved) {
      if (isLab(r.id) && labOf.has(norm(r.id).slice(0, -2))) continue
      const b = next[r.major.toUpperCase()]
      if (b) (b[r.year] ??= []).push(r.id)
    }
    setBoards(next)
    setDirty(false)
  }, [family, saved, members, labOf])

  // A different family starts on its shared defaults.
  useEffect(() => {
    setTarget(family)
    setPicked(null)
    setStatus('')
  }, [family])

  if (!semester) return <Notice>Create a semester under Upload first.</Notice>
  if (error && !majors) return <ErrorText>{error}</ErrorText>
  if (!majors || !courses || !saved) return <Loading />

  if (!families.length)
    return (
      <>
        <PageTitle title="Families" />
        <Notice>No families yet. On Majors, use Edit to give majors the same family code (for example COMP for AI, CS and CYS).</Notice>
      </>
    )

  const board = (target && boards[target]) || {}
  const shared = (family && boards[family]) || {}
  const isMajor = target !== family
  const memberCodes = new Set(members.map((m) => m.code))
  const placedYear = new Map<string, number>()
  for (const y of YEARS) for (const c of board[y] ?? []) placedYear.set(norm(c), y)

  const edit = (fn: (b: Board) => void) => {
    if (!target) return
    const b: Board = Object.fromEntries(Object.entries(board).map(([y, list]) => [y, [...list]]))
    fn(b)
    setBoards({ ...boards, [target]: b })
    setDirty(true)
    setStatus('')
  }
  /** A course sits in one year per target: placing it moves it. */
  const place = (code: string, year: number) =>
    edit((b) => {
      for (const y of YEARS) b[y] = (b[y] ?? []).filter((c) => norm(c) !== norm(code))
      b[year] = [...(b[year] ?? []), code]
    })
  const remove = (code: string) =>
    edit((b) => {
      for (const y of YEARS) b[y] = (b[y] ?? []).filter((c) => norm(c) !== norm(code))
    })

  const drag = (code: string) => (e: DragEvent) => {
    e.dataTransfer.setData('text/plain', code)
    e.dataTransfer.effectAllowed = 'move'
  }
  const drop = (to: number | 'pool') => ({
    onDragOver: (e: DragEvent) => {
      e.preventDefault()
      setOver(to)
    },
    onDragLeave: () => setOver(null),
    onDrop: (e: DragEvent) => {
      e.preventDefault()
      setOver(null)
      const code = e.dataTransfer.getData('text/plain')
      if (!code) return
      if (to === 'pool') remove(code)
      else place(code, to)
    },
  })

  const save = async () => {
    if (!family) return
    const scope = new Set([family, ...memberCodes])
    const rows = [
      ...saved.filter((r) => !scope.has(r.major.toUpperCase())),
      ...Object.entries(boards).flatMap(([t, b]) => Object.entries(b).flatMap(([y, list]) => list.map((id) => ({ id, major: t, year: Number(y) })))),
    ]
    setBusy(true)
    setError('')
    setProblems([])
    try {
      await api.put(`/admin/semesters/${semester.id}/defaults`, rows)
      setSaved(rows)
      setStatus('Saved.')
    } catch (e) {
      setError(errorText(e))
      if (e instanceof ApiError) setProblems(((e.data as { problems?: string[] } | undefined)?.problems ?? []).slice(0, 20))
    } finally {
      setBusy(false)
    }
  }

  const term = q.trim().toLowerCase()
  const pool = courses.filter(
    (c) =>
      !(isLab(c.code) && byCode.has(norm(c.code).slice(0, -2))) &&
      (!onlyFamily || c.majors.some((m) => memberCodes.has(m))) &&
      (!term || c.code.toLowerCase().replace(/\s+/g, '').includes(term.replace(/\s+/g, '')) || c.name.toLowerCase().includes(term)),
  )
  const years = YEARS.filter((y) => y < 5 || (board[y]?.length ?? 0) > 0 || (shared[y]?.length ?? 0) > 0)

  return (
    <>
      <PageTitle
        title="Families"
        sub={`Default courses in ${semester.name}. Drag a course into a year, or tap a course and then a year.`}
        action={
          <div className="flex items-center gap-3">
            {status && <span className="text-[15px] font-bold text-muted">{status}</span>}
            {dirty && !status && <span className="text-[15px] font-bold text-muted">Unsaved changes</span>}
            <Button busy={busy} disabled={!dirty} onClick={save}>
              {busy ? 'Saving…' : 'Save'}
            </Button>
          </div>
        }
      />
      <ErrorText>{error}</ErrorText>
      {problems.length > 0 && (
        <ul className="flex flex-col gap-1 text-[14px] font-semibold text-alert">
          {problems.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      )}

      <div role="tablist" aria-label="Family" className="flex flex-wrap gap-2">
        {families.map((f) => (
          <button
            key={f}
            type="button"
            role="tab"
            aria-selected={f === family}
            onClick={() => {
              if (dirty && !confirm('Leave this family without saving?')) return
              setFamily(f)
            }}
            className={cx('press h-11 rounded-full px-5 font-mono text-[15px] font-bold', f === family ? 'bg-navy text-cream' : 'tint')}
          >
            {f}
          </button>
        ))}
      </div>

      <div className="flex flex-col gap-2">
        <div role="tablist" aria-label="Whose defaults" className="flex flex-wrap gap-1.5">
          {family &&
            [{ code: family, label: `Shared · ${family}` }, ...members.map((m) => ({ code: m.code, label: `${m.code} only` }))].map((t) => (
              <button
                key={t.code}
                type="button"
                role="tab"
                aria-selected={t.code === target}
                onClick={() => {
                  setTarget(t.code)
                  setPicked(null)
                }}
                className={cx('press h-10 rounded-[12px] px-4 text-[14px] font-extrabold', t.code === target ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
              >
                {t.label}
              </button>
            ))}
        </div>
        <span className="text-[14px] font-semibold text-muted">
          {isMajor
            ? `Courses only for ${target}. A year you fill here replaces the shared one for ${target}; empty years use the shared list (shown faded).`
            : `Shared by ${members.map((m) => m.code).join(', ')}. With nothing set for a year, the upload’s core courses are ticked.`}{' '}
          Labs (codes ending in -L) go wherever their course goes.
        </span>
      </div>

      <div className="flex flex-wrap items-start gap-6">
        <section aria-label="Years" className="grid flex-[3_1_520px] grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-3">
          {years.map((y) => {
            const list = board[y] ?? []
            const fallback = isMajor && list.length === 0 ? (shared[y] ?? []) : []
            return (
              <div
                key={y}
                {...drop(y)}
                onClick={() => {
                  if (!picked) return
                  place(picked, y)
                  setPicked(null)
                }}
                className={cx(
                  'flex min-h-[180px] flex-col gap-2 rounded-[20px] p-3 transition-shadow',
                  over === y ? 'bg-ice shadow-[inset_0_0_0_2px_var(--color-navy)]' : 'tint',
                  picked && 'cursor-pointer',
                )}
              >
                <span className="flex items-center justify-between">
                  <span className="label text-[11px] tracking-[0.12em]">{yearName(y)}</span>
                  <span className="label text-[11px] text-muted">{list.length || ''}</span>
                </span>
                {list.map((code) => {
                  const c = byCode.get(norm(code))
                  return (
                    <div
                      key={code}
                      draggable
                      onDragStart={drag(code)}
                      onClick={(e) => e.stopPropagation()}
                      className="flex cursor-grab items-start gap-2 rounded-[12px] bg-paper p-2 shadow-[0_1px_0_rgb(11_25_87/0.06),0_6px_16px_-12px_rgb(11_25_87/0.4)] active:cursor-grabbing"
                    >
                      <span className="flex min-w-0 flex-1 flex-col">
                        <span className="text-[14px] leading-tight font-extrabold">{c?.name ?? code}</span>
                        <span className="font-mono text-[11px] font-bold text-muted">
                          {c?.code ?? code}
                          {labOf.has(norm(code)) && ' + lab'}
                        </span>
                      </span>
                      <button type="button" aria-label={`Remove ${code}`} onClick={() => remove(code)} className="press grid size-7 shrink-0 place-items-center rounded-full text-muted hover:text-navy">
                        <Icon name="x" className="size-3.5" strokeWidth={3} />
                      </button>
                    </div>
                  )
                })}
                {fallback.map((code) => (
                  <div key={code} className="flex flex-col rounded-[12px] border-2 border-dashed border-navy/15 p-2 opacity-55">
                    <span className="text-[14px] leading-tight font-extrabold">{byCode.get(norm(code))?.name ?? code}</span>
                    <span className="font-mono text-[11px] font-bold text-muted">Shared · {code}</span>
                  </div>
                ))}
                {list.length === 0 && fallback.length === 0 && (
                  <span className="m-auto text-center text-[13px] font-semibold text-muted">{picked ? 'Tap to put it here' : 'Drop courses here'}</span>
                )}
              </div>
            )
          })}
        </section>

        <section
          aria-label="Courses"
          {...drop('pool')}
          className={cx('flex flex-[1_1_280px] flex-col gap-2.5 rounded-[20px] p-3 lg:sticky lg:top-6', over === 'pool' ? 'bg-ice' : '')}
        >
          <TextField label="Find a course" hideLabel placeholder="Find a course" value={q} onChange={(e) => setQ(e.target.value)} />
          <label className="flex items-center gap-2 text-[14px] font-bold">
            <input type="checkbox" checked={onlyFamily} onChange={(e) => setOnlyFamily(e.target.checked)} className="size-4 accent-[var(--color-navy)]" />
            Only this family’s courses
          </label>
          <span className="text-[12px] font-semibold text-muted">Drag a placed course back here to remove it.</span>
          <div className="flex max-h-[60vh] flex-col gap-1.5 overflow-y-auto pr-1">
            {pool.length === 0 && <span className="text-[14px] font-semibold text-muted">No courses match.</span>}
            {pool.map((c) => {
              const at = placedYear.get(norm(c.code))
              return (
                <button
                  key={c.id}
                  type="button"
                  draggable
                  onDragStart={drag(c.code)}
                  onClick={() => setPicked(picked === c.code ? null : c.code)}
                  aria-pressed={picked === c.code}
                  className={cx(
                    'press flex cursor-grab items-start gap-2 rounded-[12px] p-2 text-left active:cursor-grabbing',
                    picked === c.code ? 'bg-navy text-cream' : 'bg-paper shadow-[inset_0_0_0_2px_rgb(11_25_87/0.08)]',
                  )}
                >
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="text-[14px] leading-tight font-extrabold">{c.name}</span>
                    <span className="font-mono text-[11px] font-bold opacity-70">
                      {c.code} · {c.type}
                      {labOf.has(norm(c.code)) && ' + lab'}
                    </span>
                  </span>
                  {at !== undefined && <span className="label shrink-0 text-[10px] opacity-70">{yearName(at)}</span>}
                </button>
              )
            })}
          </div>
        </section>
      </div>
    </>
  )
}
