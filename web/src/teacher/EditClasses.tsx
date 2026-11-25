import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Checkbox, ErrorText, Sk, TextField, cx } from '../components/ui'
import { BackButton } from '../student/timetable'
import { clearTeachCache } from './teachData'

/** A section as "Edit my classes" lists it. `state` null = not on my timetable. */
interface SectionRow {
  id: number
  code: string
  number: string
  instructor: string
  /** "Sun 08:00 · Tue 11:30" */
  when: string
  state: 'own' | 'added' | 'hidden' | null
}

interface Sections {
  semester: { id: number; name: string } | null
  /** Under my name and added, hidden ones included. */
  mine: SectionRow[]
  /** Last semester's added sections that exist now and aren't mine yet. */
  previous: { id: number; code: string; number: string }[]
  /** Only with a search. */
  courses: { id: number; code: string; name: string; sections: SectionRow[] }[]
}

const isOn = (r: SectionRow) => r.state === 'own' || r.state === 'added'

/**
 * Edit my classes: the sections on the teacher's timetable with a tick each, last semester's to add again, and a
 * search over every course. Each tick is saved at once. Unticking a class under my name hides it for me only;
 * unticking one I added removes it. Either stays on the page, crossed out, with Undo.
 */
export default function EditClasses() {
  const navigate = useNavigate()
  const [data, setData] = useState<Sections | null>(null)
  /** My sections as this visit has left them: an unticked one stays in its place until the page is left. */
  const [rows, setRows] = useState<SectionRow[]>([])
  const [q, setQ] = useState('')
  const [found, setFound] = useState<Sections['courses'] | null>(null)
  const [saving, setSaving] = useState<number[]>([])
  const [error, setError] = useState('')
  const asked = useRef(0)

  useEffect(() => {
    api.get<Sections>('/teach/sections').then(
      (r) => {
        setData(r)
        setRows(r.mine)
      },
      (e) => setError(errorText(e)),
    )
  }, [])

  // The search, a moment after typing stops. An answer that arrives after a newer question is dropped.
  const term = q.trim()
  useEffect(() => {
    const mine = ++asked.current
    if (!term) {
      setFound(null)
      return
    }
    const t = setTimeout(() => {
      api.get<Sections>(`/teach/sections?q=${encodeURIComponent(term)}`).then(
        (r) => mine === asked.current && setFound(r.courses),
        (e) => mine === asked.current && setError(errorText(e)),
      )
    }, 300)
    return () => clearTimeout(t)
  }, [term])

  /** Ticks a section on or off, saved at once. The server says what it now is to me. */
  const tick = async (id: number, on: boolean) => {
    if (saving.includes(id)) return
    setSaving((s) => [...s, id])
    setError('')
    try {
      const row = await api.put<SectionRow>(`/teach/sections/${id}`, { on })
      // The home shows it (or stops showing it) the next time it's opened.
      clearTeachCache()
      setRows((list) => (list.some((r) => r.id === id) ? list.map((r) => (r.id === id ? row : r)) : [...list, row]))
      setFound((courses) => courses?.map((c) => ({ ...c, sections: c.sections.map((s) => (s.id === id ? row : s)) })) ?? null)
      setData((d) => d && { ...d, previous: d.previous.filter((p) => p.id !== id) })
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving((s) => s.filter((x) => x !== id))
    }
  }

  const card = 'bg-paper shadow-[0_1px_0_rgb(11_25_87/0.06),0_8px_20px_-14px_rgb(11_25_87/0.35)]'

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(2.5rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between px-6">
          <BackButton label="Back to today" onClick={() => navigate('/teach')} />
          {data?.semester && <span className="label text-[12px] tracking-[0.14em]">{data.semester.name}</span>}
        </header>
        <div className="px-6 pt-5">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.05em]">My classes</h1>
        </div>

        <div className="px-6 pt-4" aria-live="polite">
          <ErrorText>{error}</ErrorText>
        </div>

        {!data ? (
          !error && (
            <div aria-busy="true" aria-label="Loading your classes" className="flex flex-col gap-2.5 px-6 pt-2">
              {[0, 1, 2].map((i) => (
                <Sk key={i} className="h-[76px] rounded-[20px]" />
              ))}
            </div>
          )
        ) : (
          <>
            <section aria-label="On my timetable" className="flex flex-col gap-2.5 px-6 pt-2">
              <h2 className="label text-[12px] tracking-[0.14em] text-muted">On my timetable</h2>
              {rows.map((r) => {
                const on = isOn(r)
                const tag = r.state === 'own' ? 'Under my name' : r.state === 'added' ? 'Added by me' : r.state === 'hidden' ? 'Not mine · hidden for me only' : 'Removed'
                return (
                  <div key={r.id} className={cx('flex items-center gap-3 rounded-[20px] py-3 pr-2 pl-3 transition-opacity duration-200', on ? card : 'opacity-60 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]')}>
                    <Checkbox checked={on} onChange={(v) => void tick(r.id, v)} label={`${r.code} section ${r.number}, on my timetable`} />
                    <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                      <span className={cx('text-[16px] leading-[1.15] font-extrabold', !on && 'line-through')}>
                        {r.code} <span className="font-semibold">· Section {r.number}</span>
                      </span>
                      <span className="truncate font-mono text-[11px]">{r.when || 'No times yet'}</span>
                      <span className="label text-[10px] text-muted">
                        {tag}
                        {r.state !== 'own' && r.state !== 'hidden' && r.instructor && ` · ${r.instructor}`}
                      </span>
                    </span>
                    {!on && (
                      <button type="button" onClick={() => void tick(r.id, true)} aria-label={`Undo for ${r.code} section ${r.number}`} className="press label h-11 shrink-0 px-3 text-[12px] tracking-[0.08em] underline underline-offset-[3px]">
                        Undo
                      </button>
                    )}
                  </div>
                )
              })}
              {rows.length === 0 && <p className="text-[16px] font-semibold text-muted">Nothing yet. Search below and tick the classes you teach.</p>}
            </section>

            {data.previous.length > 0 && (
              <section aria-label="Last semester" className="flex flex-col gap-2 px-6 pt-7">
                <h2 className="label text-[12px] tracking-[0.14em] text-muted">You added these last semester</h2>
                <ul className="flex flex-wrap gap-2">
                  {data.previous.map((p) => (
                    <li key={p.id}>
                      <button
                        type="button"
                        onClick={() => void tick(p.id, true)}
                        aria-label={`Add ${p.code} section ${p.number} again`}
                        className="press flex h-11 items-center gap-2 rounded-full pr-3.5 pl-4 text-[14px] font-extrabold shadow-[inset_0_0_0_2px_rgb(11_25_87/0.2)]"
                      >
                        {p.code} · {p.number}
                        <span className="label text-[10px] underline underline-offset-[3px]">Add again</span>
                      </button>
                    </li>
                  ))}
                </ul>
              </section>
            )}

            <section aria-label="Add a class" className="flex flex-col gap-3 px-6 pt-7">
              <h2 className="label text-[12px] tracking-[0.14em] text-muted">Add a class</h2>
              <TextField label="Search every course" hideLabel value={q} onChange={(e) => setQ(e.target.value)} placeholder="Course code, name or teacher" boxClassName="h-[52px] rounded-2xl" className="[&_input]:text-[17px]" />
              <p className="text-[13px] leading-snug font-semibold text-muted">You can change any class you add. The teacher it is listed under is told.</p>
              {term && !found && !error && (
                <div aria-busy="true" aria-label="Searching" className="flex flex-col gap-2.5">
                  <Sk className="h-[76px] rounded-[20px]" />
                </div>
              )}
              {found?.length === 0 && <p className="text-[15px] font-semibold">Nothing matches. Try a course code like BE1101.</p>}
              {found?.map((c) => (
                <div key={c.id} className={cx('flex flex-col rounded-[20px] px-3 pt-3 pb-1', card)}>
                  <div className="flex items-center gap-3 pb-2">
                    <span className="max-w-[40%] shrink-0 truncate rounded-[12px] bg-ice px-2 py-[7px] text-center font-mono text-[12px] font-bold">{c.code}</span>
                    <span className="min-w-0 flex-1 text-[16px] leading-[1.15] font-extrabold">{c.name}</span>
                  </div>
                  {c.sections.length === 0 && <p className="pb-2 text-[13px] font-semibold text-muted">No sections offered this semester.</p>}
                  {c.sections.map((s) => (
                    <label key={s.id} className="flex min-h-[52px] cursor-pointer items-center gap-3 border-t-[1.5px] border-navy/10 py-2">
                      <Checkbox checked={isOn(s)} onChange={(v) => void tick(s.id, v)} label={`${c.code} section ${s.number}${s.instructor ? `, ${s.instructor}` : ''}`} />
                      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                        <span className="text-[15px] leading-tight font-extrabold">
                          Section {s.number}
                          {s.instructor && <span className="font-semibold"> · {s.instructor}</span>}
                        </span>
                        <span className="truncate font-mono text-[11px]">{s.when || 'No times yet'}</span>
                      </span>
                      {s.state && <span className="label shrink-0 text-[10px] text-muted">{s.state === 'own' ? 'Mine' : s.state === 'added' ? 'Added' : 'Hidden'}</span>}
                    </label>
                  ))}
                </div>
              ))}
            </section>
          </>
        )}
      </div>
    </div>
  )
}
