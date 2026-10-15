import type { CustomSlot, Meeting, Timetable, TimetableClass, Weeks } from './types'
import { minutes } from './time'

/** Class slots: every class is 1h30 with 15-minute breaks. The week grid is laid out by slot, not by hour. */
export const SLOTS = [
  ['08:00', '09:30'],
  ['09:45', '11:15'],
  ['11:30', '13:00'],
  ['13:15', '14:45'],
  ['15:00', '16:30'],
  ['16:45', '18:15'],
] as const

/** `mine` is a student's own slot. */
export type ClassType = 'lecture' | 'lab' | 'tutorial' | 'mine'

export interface Session {
  cls: TimetableClass
  meeting: Meeting
  type: ClassType
  /** Index into SLOTS: the slot the class starts in. */
  slot: number
  /** How many slots it covers: 3 for 11:30–16:30. */
  span: number
  /** Minutes since midnight. */
  start: number
  end: number
  clash: boolean
  /** Set for the student's own slots; `cls` then holds its title so the views can show it like a class. */
  mine?: CustomSlot
  /** Notes on this session: the class's whole-class notes plus any for this date. */
  notes: number
}

/** The upload has no session type, so it comes from the room: "Lab L2" is a lab, "Tut 3" a tutorial, anything else a lecture. */
export function sessionType(room: string): ClassType {
  if (/\blab/i.test(room)) return 'lab'
  if (/\btut/i.test(room)) return 'tutorial'
  return 'lecture'
}

export const TYPE_NAME: Record<ClassType, string> = { lecture: 'Lecture', lab: 'Lab', tutorial: 'Tutorial', mine: 'My slot' }
/** Block colours: lecture cream, lab ice, tutorial sky. Text on every block is navy. */
export const TYPE_BG: Record<ClassType, string> = { lecture: 'bg-cream', lab: 'bg-ice', tutorial: 'bg-sky', mine: 'bg-paper outline-2 outline-dashed -outline-offset-2 outline-navy/50' }

/** The slot a meeting belongs to: the one it overlaps most, or the nearest one for times outside the day. */
export function slotOf(start: number, end: number) {
  let best = 0
  let bestScore = -Infinity
  SLOTS.forEach(([s, e], i) => {
    const overlap = Math.min(end, minutes(e)) - Math.max(start, minutes(s))
    const score = overlap > 0 ? overlap : -Math.abs(start - minutes(s)) - 10_000
    if (score > bestScore) {
      bestScore = score
      best = i
    }
  })
  return best
}

/** The slot a class starts in and how many slots it runs through; times outside the day fall back to the nearest slot. */
export function slotSpan(start: number, end: number) {
  const first = SLOTS.findIndex(([, e]) => start < minutes(e))
  if (first === -1) return { slot: slotOf(start, end), span: 1 }
  let last = first
  SLOTS.forEach(([s], i) => {
    if (i > first && minutes(s) < end) last = i
  })
  return { slot: first, span: last - first + 1 }
}

/** A custom slot dressed as a class, so every view can show it: negative ids keep it apart from real courses. */
function slotClass(s: CustomSlot): TimetableClass {
  const meeting: Meeting = { day: s.day, start: s.start, end: s.end, room: s.place, date: s.date }
  return {
    courseId: -s.id, code: s.title, name: s.title, type: 'Core', credits: null, changed: false,
    section: { id: -s.id, number: '', instructor: '', meetings: [meeting], changedAt: null, changeSummary: null },
  }
}

export function buildSessions(tt: Timetable): Session[] {
  const out: Session[] = []
  for (const cls of tt.classes)
    for (const meeting of cls.section.meetings) {
      const start = minutes(meeting.start)
      const end = minutes(meeting.end)
      const { slot, span } = slotSpan(start, end)
      const notes = (cls.notes?.classWide ?? 0) + (meeting.date ? (cls.notes?.dates[meeting.date] ?? 0) : 0)
      out.push({ cls, meeting, type: sessionType(meeting.room), slot, span, start, end, clash: false, notes })
    }
  for (const mine of tt.slots ?? []) {
    const cls = slotClass(mine)
    const meeting = cls.section.meetings[0]
    const start = minutes(meeting.start)
    const end = minutes(meeting.end)
    const { slot, span } = slotSpan(start, end)
    out.push({ cls, meeting, type: 'mine', slot, span, start, end, clash: false, mine, notes: mine.note ? 1 : 0 })
  }
  for (const a of out)
    a.clash = out.some(
      (b) =>
        b !== a &&
        b.cls.courseId !== a.cls.courseId &&
        b.meeting.day === a.meeting.day &&
        a.start < b.end &&
        b.start < a.end &&
        (!a.meeting.date || !b.meeting.date || a.meeting.date === b.meeting.date),
    )
  return out.sort((a, b) => a.meeting.day - b.meeting.day || a.start - b.start)
}

/** Where tapping a session goes: its course page, or the day view for the student's own slot. */
export const sessionHref = (s: Session) => (s.mine ? `/schedule?view=day&day=${s.meeting.day}` : `/course/${s.cls.courseId}`)

/** Sunday to Thursday, plus Friday or Saturday only when a class meets then. */
export const weekDays = (sessions: Session[]) => [0, 1, 2, 3, 4, 5, 6].filter((d) => d <= 4 || sessions.some((s) => s.meeting.day === d))

export const nowMinutes = (now: Date) => now.getHours() * 60 + now.getMinutes()

/** The Sunday that starts the week `offset` weeks from now. */
export function weekStart(now: Date, offset = 0) {
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - now.getDay() + offset * 7)
  return d
}

export const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)

/** "2026-10-13" for a local date. */
export const isoDate = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/** Whether a session happens on a date: a dated class only on its date, a weekly one on its weekday. */
export const onDate = (s: Session, d: Date) => (s.meeting.date ? s.meeting.date === isoDate(d) : s.meeting.day === d.getDay())

/** The sessions of the seven days from `start`: weekly classes, plus dated classes that fall in those days. */
export function inWeek(sessions: Session[], start: Date) {
  const from = isoDate(start)
  const to = isoDate(addDays(start, 6))
  return sessions.filter((s) => !s.meeting.date || (s.meeting.date >= from && s.meeting.date <= to))
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
/** "11 Oct" */
export const fmtDate = (d: Date) => `${d.getDate()} ${MONTHS[d.getMonth()]}`

/** "11 – 15 Oct", or "29 Sep – 3 Oct" across months. */
export function fmtWeekRange(start: Date, days: number) {
  const end = addDays(start, days - 1)
  return start.getMonth() === end.getMonth() ? `${start.getDate()} – ${fmtDate(end)}` : `${fmtDate(start)} – ${fmtDate(end)}`
}

/** Teaching week number of the week starting `sunday`: 1 for week 1's Sunday, counting on from there. */
export function teachingWeek(sunday: Date, week1Start: string) {
  const [y, m, d] = week1Start.split('-').map(Number)
  return Math.round((sunday.getTime() - new Date(y, m - 1, d).getTime()) / (7 * 86_400_000)) + 1
}

/** "Week 4" from the semester's teaching weeks; "This week" / "In 3 weeks" when they aren't known. */
export function weekTitle(offset: number, sunday?: Date, weeks?: Weeks | null) {
  if (sunday && weeks?.week1Start) {
    const n = teachingWeek(sunday, weeks.week1Start)
    if (n < 1) return 'Before week 1'
    if (weeks.lastWeek && n > weeks.lastWeek) return `After week ${weeks.lastWeek}`
    return `Week ${n}`
  }
  if (offset === 0) return 'This week'
  if (offset === 1) return 'Next week'
  if (offset === -1) return 'Last week'
  return offset > 0 ? `In ${offset} weeks` : `${-offset} weeks ago`
}

/** 95 → "1h 35m", 90 → "1h 30m", 45 → "45m" */
export function fmtDuration(min: number) {
  const h = Math.floor(min / 60)
  const m = min % 60
  return h === 0 ? `${m}m` : m === 0 ? `${h}h` : `${h}h ${m}m`
}

/** "9:45" style 24-hour clock from minutes. */
export const clock = (min: number) => `${String(Math.floor(min / 60)).padStart(2, '0')}:${String(min % 60).padStart(2, '0')}`

export const initials = (name: string) =>
  name
    .replace(/^(prof|dr|mr|mrs|ms)\.?\s+/i, '')
    .split(/\s+/)
    .filter(Boolean)
    .map((w) => w[0])
    .slice(0, 2)
    .join('')
    .toUpperCase()
