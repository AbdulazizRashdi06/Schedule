import type { Course, Section } from '../lib/types'
import { DAY_SHORT, meetingsOverlap, sectionsClash } from '../lib/time'
import { Icon, cx } from './ui'

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

/** The first clash for a picked section, for "Clashes with ML on Sun 09:45". */
export function clashDetail(section: Section, courseId: number, picks: Picks, byId: Map<number, Course>): { code: string; at: string } | null {
  for (const m of section.meetings)
    for (const [cid, sid] of picks) {
      if (cid === courseId || sid == null) continue
      const other = byId.get(cid)
      const s = other?.sections.find((x) => x.id === sid)
      const hit = s?.meetings.find((o) => meetingsOverlap(m, o))
      if (other && hit) return { code: other.code, at: `${DAY_SHORT[m.day]} ${m.start}` }
    }
  return null
}

/** "Sun 09:45 · Tue 09:45" */
export const meetsText = (s: Section, sep = '  ·  ') => s.meetings.map((m) => `${DAY_SHORT[m.day]} ${m.start}`).join(sep)

const kind = (c: Course) => (c.type === 'Elective' ? 'Elective' : c.type)

/** Section chips [1][2][3]: navy when picked, dark red when the pick clashes. */
export function SectionChips({ course, value, clash, onPick, size = 44 }: { course: Course; value: number | null; clash: boolean; onPick: (sectionId: number) => void; size?: 40 | 44 }) {
  return (
    <div role="radiogroup" aria-label={`${course.name} section`} className="flex flex-wrap gap-1.5">
      {course.sections.map((s) => {
        const on = s.id === value
        return (
          <button
            key={s.id}
            type="button"
            role="radio"
            aria-checked={on}
            aria-label={`Section ${s.number}, ${meetsText(s, ', ')}`}
            onClick={() => onPick(s.id)}
            className={cx(
              'press grid min-w-11 place-items-center px-2 font-mono font-bold',
              size === 44 ? 'h-11 rounded-[14px] text-[16px]' : 'h-10 min-w-10 rounded-[12px] text-[15px]',
              on ? (clash ? 'bg-alert-ink text-cream' : 'bg-navy text-cream') : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.2)]',
            )}
          >
            {s.number}
          </button>
        )
      })}
    </div>
  )
}

export function ClashNotice({ children }: { children: React.ReactNode }) {
  return (
    <div role="alert" className="open flex items-start gap-2 rounded-[12px] bg-alert-bg px-2.5 py-2 text-[13px] leading-[1.3] font-bold text-alert-ink">
      <Icon name="warn" className="mt-px size-4" strokeWidth={2.6} />
      <span>{children}</span>
    </div>
  )
}

/** A course in setup: tick box, title, then section chips and meeting times once ticked. */
export function CourseRow({
  course,
  picks,
  byId,
  onChange,
  note,
  style,
}: {
  course: Course
  picks: Picks
  byId: Map<number, Course>
  onChange: (next: Picks) => void
  note?: string
  style?: React.CSSProperties
}) {
  const picked = picks.has(course.id)
  const sectionId = picks.get(course.id) ?? null
  const selected = course.sections.find((s) => s.id === sectionId)
  const clash = selected ? clashDetail(selected, course.id, picks, byId) : null

  const set = (fn: (m: Picks) => void) => {
    const next = new Map(picks)
    fn(next)
    onChange(next)
  }
  const toggle = () =>
    set((m) => {
      if (!picked) m.set(course.id, course.sections.length === 1 ? course.sections[0].id : null)
      else m.delete(course.id)
    })

  return (
    <div
      style={style}
      className={cx(
        'rise flex flex-col gap-2.5 rounded-[22px] p-3.5 transition-[box-shadow,background-color,opacity] duration-200',
        picked
          ? cx('bg-paper', clash ? 'shadow-[0_0_0_2px_var(--color-alert-line)]' : 'shadow-[0_0_0_2px_rgb(11_25_87/0.1)]')
          : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]',
      )}
    >
      <div className="flex items-center gap-3">
        <button
          type="button"
          role="checkbox"
          aria-checked={picked}
          aria-label={`${picked ? 'Drop' : 'Add'} ${course.name}`}
          onClick={toggle}
          className={cx('press grid size-8 shrink-0 place-items-center rounded-[10px]', picked ? 'bg-navy text-cream' : 'border-2 border-navy/35')}
        >
          {picked && <Icon name="check" className="pop size-4" strokeWidth={3.4} />}
        </button>
        <button type="button" tabIndex={-1} onClick={toggle} className="flex min-w-0 flex-1 flex-col gap-0.5 text-left">
          <span className="text-[17px] leading-[1.1] font-extrabold">{course.name}</span>
          <span className="label text-[11px] tracking-[0.08em] opacity-75">
            {course.code} · {kind(course)}
            {course.credits != null && ` · ${course.credits} credits`}
          </span>
        </button>
      </div>
      {note && (
        <span className="label ml-11 flex items-center gap-1.5 self-start rounded-full bg-ice px-2.5 py-1 text-[10px]">
          <Icon name="redo" className="size-3" strokeWidth={2.8} />
          {note}
        </span>
      )}
      {picked &&
        (course.sections.length === 0 ? (
          <p className="pl-11 text-[13px] font-semibold text-muted">No sections offered this semester.</p>
        ) : (
          <div className="open flex flex-col gap-2.5">
            <div className="flex items-center gap-2 pl-11">
              <span className="label text-[10px] tracking-[0.12em] opacity-70">Sec</span>
              <SectionChips course={course} value={sectionId} clash={!!clash} onPick={(id) => set((m) => m.set(course.id, id))} />
            </div>
            <span className="pl-11 font-mono text-[11px] font-medium">{selected ? meetsText(selected) : 'Pick a section'}</span>
            {clash && (
              <div className="ml-11">
                <ClashNotice>
                  Clashes with {clash.code} on {clash.at}. Fine if you have a clash waiver, or pick another section.
                </ClashNotice>
              </div>
            )}
          </div>
        ))}
    </div>
  )
}
