import { useEffect, useMemo, useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, Me, Options, PickRow } from '../lib/types'
import { yearName } from '../lib/year'
import { ErrorText, Sheet, Sk, TextField, Toast, cx, useToast } from '../components/ui'
import { ClashNotice, SectionChips, clashDetail, meetsText, type Picks } from '../components/CourseRow'
import { BackButton, clearTimetableCache } from './timetable'
import ImportCourses from './ImportCourses'

const TYPE_ORDER = { Core: 0, LS: 1, Elective: 2 } as const
const KIND = { Core: 'Core', LS: 'LS', Elective: 'Elective' } as const

/** Part D: every pick on one page, saved once. Dropped rows stay crossed out (with Undo) so nothing jumps. */
export default function EditCourses() {
  const navigate = useNavigate()
  const [me, setMe] = useState<Me | null>(null)
  const [options, setOptions] = useState<Options | null>(null)
  const [picks, setPicks] = useState<Picks>(new Map())
  const [saved, setSaved] = useState<Picks>(new Map())
  /** Saved courses the student dropped, with the section to bring back on Undo. */
  const [dropped, setDropped] = useState<Picks>(new Map())
  const [adding, setAdding] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const toast = useToast(2400)

  const load = () =>
    api
      .get<Me>('/me')
      .then(async (m) => {
        setMe(m)
        if (!m.setupDone) return
        const [o, p] = await Promise.all([api.get<Options>('/me/options'), api.get<PickRow[]>('/me/picks')])
        setOptions(o)
        const map: Picks = new Map(p.map((x) => [x.courseId, x.sectionId]))
        setPicks(map)
        setSaved(map)
        setDropped(new Map())
      })
      .catch((e) => setError(errorText(e)))
  useEffect(() => {
    void load()
  }, [])

  const byId = useMemo(() => new Map((options?.courses ?? []).map((c) => [c.id, c])), [options])
  if (me && !me.setupDone) return <Navigate to="/setup" replace />

  const order = (a: Course, b: Course) => TYPE_ORDER[a.type] - TYPE_ORDER[b.type] || a.code.localeCompare(b.code)
  const savedRows = [...saved.keys()].map((id) => byId.get(id)).filter((c): c is Course => !!c).sort(order)
  const addedRows = [...picks.keys()].filter((id) => !saved.has(id)).map((id) => byId.get(id)).filter((c): c is Course => !!c)
  const rows = [...savedRows, ...addedRows]

  let changes = addedRows.length
  for (const [id, sid] of saved) if (!picks.has(id) || picks.get(id) !== sid) changes++
  const missing = rows.filter((c) => picks.has(c.id) && picks.get(c.id) == null && c.sections.length > 0).map((c) => c.code)
  // Clashes are allowed (some students have a clash waiver); the cards still flag them.
  const blocker = missing.length > 0 ? `Pick a section for ${missing.join(', ')}` : picks.size === 0 ? 'Keep at least one course' : null

  const drop = (c: Course) => {
    const next = new Map(picks)
    if (saved.has(c.id)) setDropped(new Map(dropped).set(c.id, picks.get(c.id) ?? null))
    next.delete(c.id)
    setPicks(next)
  }
  const undo = (c: Course) => {
    setPicks(new Map(picks).set(c.id, dropped.get(c.id) ?? saved.get(c.id) ?? null))
    const d = new Map(dropped)
    d.delete(c.id)
    setDropped(d)
  }

  const saveAll = async () => {
    if (blocker || busy) return
    setBusy(true)
    setError('')
    try {
      await api.put('/me/picks', { picks: [...picks].map(([courseId, sectionId]) => ({ courseId, sectionId })) })
      clearTimetableCache()
      setSaved(new Map(picks))
      setDropped(new Map())
      toast.show('Saved. Your week is updated.')
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.75rem,env(safe-area-inset-top))] pb-36">
        <header className="flex items-center justify-between px-6">
          <BackButton label="Back to today" onClick={() => navigate('/')} />
          {options && <span className="label text-[12px] tracking-[0.14em]">{options.semester.name}</span>}
        </header>
        <div className="flex items-end justify-between gap-3 px-6 pt-5">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.05em]">My courses</h1>
          <button type="button" onClick={() => setAdding(true)} disabled={!options} className="press h-11 shrink-0 rounded-full bg-navy px-4 text-[15px] font-extrabold text-cream">
            Add course
          </button>
        </div>
        {me?.canImport && options && (
          <div className="px-6 pt-4">
            <ImportCourses onDone={load} />
          </div>
        )}

        {error && !options ? (
          <div className="px-6 pt-6">
            <ErrorText>{error}</ErrorText>
          </div>
        ) : !options ? (
          <div aria-busy="true" aria-label="Loading your courses" className="flex flex-col gap-2.5 px-6 pt-[18px]">
            {[0, 1, 2, 3].map((i) => (
              <Sk key={i} className="h-[112px] rounded-[20px]" />
            ))}
          </div>
        ) : (
          <div className="flex flex-col gap-2.5 px-6 pt-[18px]">
            {rows.map((c) => {
              const isDropped = !picks.has(c.id)
              const isNew = !saved.has(c.id)
              const sid = picks.get(c.id) ?? null
              const section = c.sections.find((s) => s.id === sid)
              const clash = !isDropped && section ? clashDetail(section, c.id, picks, byId) : null
              const tag = isDropped ? 'Dropped' : isNew ? 'New' : saved.get(c.id) !== sid ? 'Section changed' : KIND[c.type]
              return (
                <div
                  key={c.id}
                  className={cx(
                    'flex flex-col gap-2.5 rounded-[20px] py-3 pr-2 pl-3 transition-opacity duration-200',
                    isDropped
                      ? 'opacity-60 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]'
                      : cx('bg-paper', clash ? 'shadow-[0_1px_0_rgb(11_25_87/0.06),0_8px_20px_-14px_rgb(11_25_87/0.35),0_0_0_2px_var(--color-alert-line)]' : 'shadow-[0_1px_0_rgb(11_25_87/0.06),0_8px_20px_-14px_rgb(11_25_87/0.35)]'),
                  )}
                >
                  <div className="flex items-center gap-3">
                    <span className="w-[52px] shrink-0 truncate rounded-[12px] bg-ice px-1 py-[7px] text-center font-mono text-[12px] font-bold">{c.code}</span>
                    <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                      <span className={cx('text-[16px] leading-[1.15] font-extrabold', isDropped && 'line-through')}>{c.name}</span>
                      <span className="label text-[10px] text-muted">{tag}</span>
                    </span>
                    <button
                      type="button"
                      onClick={() => (isDropped ? undo(c) : drop(c))}
                      aria-label={`${isDropped ? 'Undo drop of' : 'Drop'} ${c.name}`}
                      className="press label h-11 shrink-0 px-3 text-[12px] tracking-[0.08em] underline underline-offset-[3px]"
                    >
                      {isDropped ? 'Undo' : 'Drop'}
                    </button>
                  </div>
                  {!isDropped &&
                    (c.sections.length === 0 ? (
                      <p className="pl-16 text-[13px] font-semibold text-muted">No sections offered this semester.</p>
                    ) : (
                      <>
                        <div className="flex flex-wrap items-center gap-1.5 pl-16">
                          <SectionChips course={c} value={sid} clash={!!clash} size={40} onPick={(id) => setPicks(new Map(picks).set(c.id, id))} />
                          <span className="ml-1 font-mono text-[11px]">{section ? meetsText(section, ', ') : 'Pick a section'}</span>
                        </div>
                        {clash && (
                          <div className="ml-16">
                            <ClashNotice>
                              Clashes with {clash.code} on {clash.at}. Fine if you have a clash waiver.
                            </ClashNotice>
                          </div>
                        )}
                      </>
                    ))}
                </div>
              )
            })}
            {rows.length === 0 && <p className="text-[16px] font-semibold text-muted">No courses yet. Add one to get started.</p>}
          </div>
        )}
      </div>

      <Toast toast={toast} className="inset-x-6 bottom-6 mx-auto max-w-[432px] rounded-[20px] bg-ice px-[18px] py-4 text-[16px] font-extrabold shadow-[0_12px_24px_-14px_rgb(11_25_87/0.5)]" />
      <div
        data-shown={changes > 0 && !toast.shown}
        className="bar-up fixed inset-x-3 bottom-[max(1rem,env(safe-area-inset-bottom))] z-30 mx-auto flex max-w-[456px] items-center justify-between gap-3 rounded-[28px] bg-navy py-3 pr-3 pl-5 text-cream shadow-[0_16px_32px_-16px_rgb(11_25_87/0.7)]"
      >
        <span className="flex min-w-0 flex-col gap-0.5">
          <span className="text-[17px] font-extrabold">
            {changes} {changes === 1 ? 'change' : 'changes'}
          </span>
          <span className="label truncate text-[11px] font-medium tracking-[0.06em] text-ice" aria-live="polite">
            {error || blocker || 'Not saved yet'}
          </span>
        </span>
        <button
          type="button"
          onClick={saveAll}
          aria-disabled={!!blocker || busy}
          className={cx('press h-12 shrink-0 rounded-full px-6 text-[16px] font-extrabold', blocker ? 'bg-navy-700 text-[#9AA3C7]' : 'bg-sky text-navy')}
        >
          {busy ? 'Saving…' : 'Save'}
        </button>
      </div>

      {options && me && (
        <AddSheet
          open={adding}
          courses={options.courses.filter((c) => !picks.has(c.id) && !dropped.has(c.id))}
          me={me}
          onClose={() => setAdding(false)}
          onAdd={(c) => {
            setPicks(new Map(picks).set(c.id, c.sections.length === 1 ? c.sections[0].id : null))
            setAdding(false)
          }}
        />
      )}
    </div>
  )
}

function AddSheet({ open, courses, me, onClose, onAdd }: { open: boolean; courses: Course[]; me: Me; onClose: () => void; onAdd: (c: Course) => void }) {
  const [q, setQ] = useState('')
  const term = q.trim().toLowerCase()
  const list = courses
    .filter((c) => !term || c.code.toLowerCase().includes(term.replace(/\s/g, '')) || c.name.toLowerCase().includes(term))
    .sort((a, b) => TYPE_ORDER[a.type] - TYPE_ORDER[b.type] || (a.type === 'Core' ? Math.abs((a.year ?? 9) - (me.year ?? 0)) - Math.abs((b.year ?? 9) - (me.year ?? 0)) : 0) || a.code.localeCompare(b.code))
  return (
    <Sheet open={open} onClose={onClose} label="Add a course" className="h-[600px] gap-4">
      <div className="flex items-center justify-between">
        <h2 className="text-[28px] font-extrabold tracking-[-0.03em]">Add a course</h2>
        <button type="button" onClick={onClose} className="label h-11 px-1 text-[12px] tracking-[0.08em]">
          Close
        </button>
      </div>
      <TextField label="Search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name or code" boxClassName="h-[52px] rounded-2xl" className="[&_input]:text-[17px]" />
      <div className="flex flex-col">
        {list.map((c, i) => (
          <div key={c.id} className={cx('flex items-center gap-3 py-3', i > 0 && 'border-t-[1.5px] border-navy/10')}>
            <span className="flex min-w-0 flex-1 flex-col gap-0.5">
              <span className="text-[16px] font-extrabold">{c.name}</span>
              <span className="label text-[10px] text-muted">
                {c.code} · {KIND[c.type]}
                {c.type === 'Core' && c.year != null ? ` · ${yearName(c.year)}` : ''}
              </span>
            </span>
            <button type="button" onClick={() => onAdd(c)} aria-label={`Add ${c.name}`} className="press h-10 shrink-0 rounded-full bg-ice px-4 text-[14px] font-extrabold">
              Add
            </button>
          </div>
        ))}
        {list.length === 0 && <p className="pt-3 text-[15px] font-semibold">Nothing matches. Only courses open to your major show here.</p>}
      </div>
    </Sheet>
  )
}
