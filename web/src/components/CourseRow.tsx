import { useState } from 'react'
import type { Course, Section } from '../lib/types'
import { fmtRange, DAY_LONG, sectionSummary, sectionsClash } from '../lib/time'
import { Badge, Checkbox, Sheet, XIcon, cx } from './ui'

/** courseId → picked sectionId (null: course ticked, no section yet). */
export type Picks = Map<number, number | null>

/** Codes of other picked courses whose section overlaps this one. */
export function clashesFor(section: Section, courseId: number, picks: Picks, byId: Map<number, Course>): string[] {
  const out: string[] = []
  for (const [cid, sid] of picks) {
    if (cid === courseId || sid == null) continue
    const other = byId.get(cid)
    const s = other?.sections.find((x) => x.id === sid)
    if (other && s && sectionsClash(section, s)) out.push(other.code)
  }
  return out
}

/**
 * One course with its sections as chips:  ☑ CS201 Data Structures  [1] [2] [3]
 * Picking a clashing section is allowed; the chip just shows ⚠.
 */
export function CourseRow({
  course,
  picks,
  byId,
  onChange,
  mode = 'check',
  note,
}: {
  course: Course
  picks: Picks
  byId: Map<number, Course>
  onChange: (next: Picks) => void
  /** check: a tick box; remove: an ✕ (Edit my courses). */
  mode?: 'check' | 'remove'
  note?: string
}) {
  const [details, setDetails] = useState<Section | null>(null)
  const picked = picks.has(course.id)
  const sectionId = picks.get(course.id) ?? null
  const selected = course.sections.find((s) => s.id === sectionId)

  const set = (fn: (m: Picks) => void) => {
    const next = new Map(picks)
    fn(next)
    onChange(next)
  }
  const toggle = (on: boolean) =>
    set((m) => {
      if (on) m.set(course.id, course.sections.length === 1 ? course.sections[0].id : null)
      else m.delete(course.id)
    })

  const selectedClashes = selected ? clashesFor(selected, course.id, picks, byId) : []

  return (
    <div className={cx('rounded-2xl bg-white p-3.5 ring-1 dark:bg-zinc-900', picked ? 'ring-zinc-200 dark:ring-zinc-800' : 'ring-zinc-200/60 dark:ring-zinc-800/60')}>
      <div className="flex items-start gap-3">
        {mode === 'check' && <Checkbox checked={picked} onChange={toggle} label={`Take ${course.code}`} />}
        <div className="min-w-0 flex-1" onClick={() => mode === 'check' && !picked && toggle(true)}>
          <div className="flex flex-wrap items-baseline gap-x-2">
            <span className="font-semibold">{course.code}</span>
            <span className="text-[15px] text-zinc-600 dark:text-zinc-400">{course.name}</span>
          </div>
          <div className="mt-0.5 flex flex-wrap gap-1.5 text-xs text-zinc-500">
            {course.credits != null && <span>{course.credits} credit{course.credits === 1 ? '' : 's'}</span>}
            {note && <Badge tone="indigo">{note}</Badge>}
            {course.sections.length === 0 && <span>No sections offered</span>}
          </div>
        </div>
        {mode === 'remove' && (
          <button onClick={() => toggle(false)} className="-m-1 rounded-lg p-1.5 text-zinc-400 hover:bg-zinc-100 hover:text-zinc-700 dark:hover:bg-zinc-800" aria-label={`Remove ${course.code}`}>
            <XIcon />
          </button>
        )}
      </div>

      {picked && course.sections.length > 0 && (
        <div className={cx('mt-3 space-y-2', mode === 'check' && 'pl-9')}>
          <div className="flex flex-wrap gap-2" role="radiogroup" aria-label={`${course.code} section`}>
            {course.sections.map((s) => {
              const on = s.id === sectionId
              const clash = clashesFor(s, course.id, picks, byId).length > 0
              return (
                <button
                  key={s.id}
                  role="radio"
                  aria-checked={on}
                  onClick={() => (on ? setDetails(s) : set((m) => m.set(course.id, s.id)))}
                  className={cx(
                    'flex min-w-11 flex-col items-start rounded-xl px-3 py-1.5 text-left ring-1 transition active:scale-[0.97]',
                    on
                      ? 'bg-indigo-600 text-white ring-indigo-600 dark:bg-indigo-500 dark:ring-indigo-500'
                      : 'bg-zinc-50 ring-zinc-200 hover:ring-zinc-300 dark:bg-zinc-800/60 dark:ring-zinc-700',
                  )}
                >
                  <span className="flex items-center gap-1 text-[15px] font-semibold">
                    {s.number}
                    {clash && <span title="Clashes with another pick" className={on ? 'text-amber-200' : 'text-amber-600 dark:text-amber-400'}>⚠</span>}
                  </span>
                  <span className={cx('text-[11px] leading-tight', on ? 'text-indigo-100' : 'text-zinc-500')}>{sectionSummary(s, true)}</span>
                </button>
              )
            })}
          </div>
          {selected ? (
            <button onClick={() => setDetails(selected)} className="block text-left text-[13px] text-zinc-500">
              {selected.instructor || 'Instructor TBA'}
              {selected.meetings.some((m) => m.room) && ` · ${[...new Set(selected.meetings.map((m) => m.room).filter(Boolean))].join(', ')}`}
              <span className="ml-1 text-indigo-600 dark:text-indigo-400">Details</span>
            </button>
          ) : (
            <p className="text-[13px] text-amber-700 dark:text-amber-400">Pick a section</p>
          )}
          {selectedClashes.length > 0 && (
            <p className="text-[13px] text-amber-700 dark:text-amber-400">⚠ Clashes with {selectedClashes.join(', ')}. You can keep it, but check with your advisor.</p>
          )}
        </div>
      )}

      <Sheet open={!!details} onClose={() => setDetails(null)} title={details && `${course.code} · Section ${details.number}`}>
        {details && <SectionDetails course={course} section={details} />}
      </Sheet>
    </div>
  )
}

export function SectionDetails({ course, section }: { course: { code: string; name: string }; section: Section }) {
  return (
    <div className="space-y-4 text-[15px]">
      <p className="text-zinc-600 dark:text-zinc-400">{course.name}</p>
      <dl className="space-y-3">
        <div>
          <dt className="text-xs font-medium tracking-wide text-zinc-500 uppercase">Instructor</dt>
          <dd>{section.instructor || 'To be announced'}</dd>
        </div>
        <div>
          <dt className="text-xs font-medium tracking-wide text-zinc-500 uppercase">Times</dt>
          <dd className="space-y-1">
            {section.meetings.map((m, i) => (
              <div key={i} className="flex justify-between gap-3">
                <span>
                  {DAY_LONG[m.day]} · {fmtRange(m)}
                </span>
                <span className="text-zinc-500">{m.room || 'Room TBA'}</span>
              </div>
            ))}
          </dd>
        </div>
      </dl>
    </div>
  )
}
