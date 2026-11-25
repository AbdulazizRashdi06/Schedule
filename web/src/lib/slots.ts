import type { ClassChange, CustomSlot, Meeting, OfficeHour, Timetable, TimetableClass, Weeks } from './types'
import { meetingsOverlap, minutes } from './time'

/** Class slots: every class is 1h30 with 15-minute breaks. The week grid is laid out by slot, not by hour. */
export const SLOTS = [
  ['08:00', '09:30'],
  ['09:45', '11:15'],
  ['11:30', '13:00'],
  ['13:15', '14:45'],
  ['15:00', '16:30'],
  ['16:45', '18:15'],
] as const

/** `mine` is a student's (or teacher's) own slot; `office` is a teacher's office hours on their own timetable. */
export type ClassType = 'lecture' | 'lab' | 'tutorial' | 'mine' | 'exam' | 'office'

export interface Session {
  plannerEventId?: number
  plannerNotes?: string | null
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
  /** Clashes with another course's weekly class or slot: every week both meet. Use clashOn() for a date. */
  clash: boolean
  /** Dates ("2026-11-03") it clashes with another course's dated class, event or one-off slot. */
  clashDates: string[]
  /** Set for the student's own slots; `cls` then holds its title so the views can show it like a class. */
  mine?: CustomSlot
  /** Notes on this session: the class's whole-class notes plus any for this date. For a weekly class use notesOn(). */
  notes: number
  /** Note counts per date ("2026-11-03"), for each week a weekly class meets. */
  noteDates: Record<string, number>
  /** For a weekly class or slot: the semester's first and last teaching day ("2026-09-06"), outside which it doesn't meet. */
  firstDate?: string
  lastDate?: string
  /** The teacher's change that put this session on (a moved, re-roomed or extra class). */
  change?: ClassChange
  /** A ghost: drawn in its place, greyed, but not happening. */
  off?: 'cancelled' | 'moved'
  /** Teacher side: the sessions (one per course code) that are this one real class, its own included. See blockOn(). */
  merged?: Session[]
  /** Teacher side: set for office hours; `cls` is then a stand-in, like an own slot's. */
  office?: OfficeHour
}

/** Notes on a session as it falls on a date: whole-class notes plus that day's session notes, weekly classes included. */
export const notesOn = (s: Session, date: Date) => s.notes + (s.meeting.date ? 0 : (s.noteDates[isoDate(date)] ?? 0))

/** The upload has no session type, so it comes from the room: "Lab L2" is a lab, "Tut 3" a tutorial, anything else a lecture. */
export function sessionType(room: string): ClassType {
  if (/\blab/i.test(room)) return 'lab'
  if (/\btut/i.test(room)) return 'tutorial'
  return 'lecture'
}

export const TYPE_NAME: Record<ClassType, string> = { lecture: 'Lecture', lab: 'Lab', tutorial: 'Tutorial', mine: 'My slot', exam: 'Exam', office: 'Office hours' }
/** Block colours: lecture cream, lab ice, tutorial sky. Text on every block is navy. */
export const TYPE_BG: Record<ClassType, string> = { lecture: 'bg-cream', lab: 'bg-ice', tutorial: 'bg-sky', mine: 'bg-paper outline-2 outline-dashed -outline-offset-2 outline-navy/50', exam: 'bg-sky outline-2 -outline-offset-2 outline-navy', office: 'bg-paper outline-2 -outline-offset-2 outline-navy' }

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

/**
 * Every session of a timetable. `clashes: false` leaves the clash marks to the caller (the teacher side collapses
 * merged classes first, then calls markClashes itself).
 */
export function buildSessions(tt: Timetable, opts?: { clashes?: boolean }): Session[] {
  const out: Session[] = []
  for (const cls of tt.classes)
    for (const meeting of cls.section.meetings) {
      const start = minutes(meeting.start)
      const end = minutes(meeting.end)
      const { slot, span } = slotSpan(start, end)
      const notes = (cls.notes?.classWide ?? 0) + (meeting.date ? (cls.notes?.dates[meeting.date] ?? 0) : 0)
      const change = meeting.changeId == null ? undefined : tt.changes?.find((c) => c.id === meeting.changeId)
      out.push({ cls, meeting, type: meeting.type ?? sessionType(meeting.room), slot, span, start, end, clash: false, clashDates: [], notes, noteDates: cls.notes?.dates ?? {}, ...(change && { change }) })
    }
  for (const mine of tt.slots ?? []) {
    const cls = slotClass(mine)
    const meeting = cls.section.meetings[0]
    const start = minutes(meeting.start)
    const end = minutes(meeting.end)
    const { slot, span } = slotSpan(start, end)
    out.push({ cls, meeting, type: 'mine', slot, span, start, end, clash: false, clashDates: [], mine, notes: mine.note ? 1 : 0, noteDates: {} })
  }
  for (const event of tt.plannerEvents ?? []) {
    const original = tt.classes.find(c => c.courseId === event.courseId)
    const meeting: Meeting = { day: new Date(`${event.date}T12:00`).getDay(), start: event.start, end: event.end, room: event.room, date: event.date }
    const cls: TimetableClass = { courseId: event.courseId, code: event.code, name: `${event.title} · ${event.courseName}`, type: original?.type ?? 'Core', credits: original?.credits ?? null, changed: false,
      section: { id: event.sectionId ?? 0, number: original?.section.number ?? '', instructor: original?.section.instructor ?? '', meetings: [meeting], changedAt: event.updatedAt, changeSummary: 'Published by a batch representative' } }
    const start = minutes(event.start), end = minutes(event.end)
    out.push({ cls, meeting, type: event.kind === 'exam' ? 'exam' : sessionType(event.room), ...slotSpan(start, end), start, end, clash: false, clashDates: [], notes: 0, noteDates: {}, plannerEventId: event.id, plannerNotes: event.notes })
  }
  // Weekly classes and slots only meet in the teaching weeks (planner events are all dated, so they're left alone).
  if (tt.span)
    for (const s of out)
      if (!s.meeting.date) {
        s.firstDate = tt.span.first
        s.lastDate = tt.span.last
      }
  if (opts?.clashes !== false) markClashes(out)
  return sortSessions(out)
}

/** By weekday, then start time: the order every list of sessions is kept in. */
export const sortSessions = (sessions: Session[]) => sessions.sort((a, b) => a.meeting.day - b.meeting.day || a.start - b.start)

/**
 * Marks which sessions clash. `same` says when two sessions can't clash with each other: for a student, two sessions
 * of one course. It clears the marks first, so it can be run again.
 */
export function markClashes(sessions: Session[], same: (a: Session, b: Session) => boolean = (a, b) => a.cls.courseId === b.cls.courseId) {
  for (const s of sessions) {
    s.clash = false
    s.clashDates = []
  }
  // A clash needs both on at once. Two weekly ones clash every week; with a dated one, only on its date, and only when
  // the other meets that day too (not a day off, a cancelled class, or outside the teaching weeks).
  for (const a of sessions)
    for (const b of sessions) {
      if (b === a || same(a, b) || !meetingsOverlap(a.meeting, b.meeting)) continue
      const date = a.meeting.date ?? b.meeting.date
      if (!date) a.clash = true
      else if (!a.clashDates.includes(date) && onDate(a, new Date(`${date}T12:00`)) && onDate(b, new Date(`${date}T12:00`))) a.clashDates.push(date)
    }
}

/**
 * Teacher side: the course codes of a merged class that meet on a date. One code can be changed on its own for one
 * week, so on that date the block is the codes still on. A plain session is just itself.
 */
export const blockOn = (s: Session, date?: Date): Session[] => (!s.merged ? [s] : date ? s.merged.filter((m) => onDate(m, date)) : s.merged)

/** "BE1101", or "BE1101 · IBSM1101 · LOG1101" for one real class listed under several codes. */
export function sessionCode(s: Session, date?: Date) {
  if (!s.merged) return s.cls.code
  const on = blockOn(s, date)
  return (on.length ? on : s.merged).map((m) => m.cls.code).join(' · ')
}

/** Whether a session clashes with another course as it falls on a date. */
export const clashOn = (s: Session, date: Date) => s.clash || s.clashDates.includes(isoDate(date))

/** Where tapping a session goes outside home: its course page, or home for the student's own slot. */
export const sessionHref = (s: Session) => (s.mine ? '/' : `/course/${s.cls.courseId}`)

/** Sunday to Thursday, plus Friday or Saturday only when a class meets then. */
export const weekDays = (sessions: Session[]) => [0, 1, 2, 3, 4, 5, 6].filter((d) => d <= 4 || sessions.some((s) => s.meeting.day === d))

export const nowMinutes = (now: Date) => now.getHours() * 60 + now.getMinutes()

/** Oman is UTC+4 all year (no daylight saving). Class times are Oman times, and the server counts days in Oman time. */
const OMAN_MS = 4 * 3_600_000

/**
 * An instant as a clock on campus shows it: a Date whose own getters (getDate, getDay, getHours…) read Oman's date and
 * time, whatever time zone the device is set to. Read its fields only; its timestamp is shifted.
 */
export function campusTime(at: Date) {
  const o = new Date(at.getTime() + OMAN_MS)
  return new Date(o.getUTCFullYear(), o.getUTCMonth(), o.getUTCDate(), o.getUTCHours(), o.getUTCMinutes(), o.getUTCSeconds())
}

/** Now, on campus. Use this, not new Date(), for today, this week and the time of day. */
export const campusNow = () => campusTime(new Date())

/** The Sunday that starts the week `offset` weeks from now. */
export function weekStart(now: Date, offset = 0) {
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - now.getDay() + offset * 7)
  return d
}

export const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)

/** "2026-10-13" for a local date. */
export const isoDate = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/** Whether a session happens on a date: a dated class only on its date, a weekly one on its weekday within the teaching weeks. */
export function onDate(s: Session, d: Date) {
  const iso = isoDate(d)
  if (s.meeting.excludedDates?.includes(iso)) return false
  if (s.meeting.date) return s.meeting.date === iso
  return s.meeting.day === d.getDay() && !(s.firstDate && iso < s.firstDate) && !(s.lastDate && iso > s.lastDate)
}

/** The sessions of the seven days from `start`: weekly classes meeting that week, plus dated classes that fall in those days. */
export function inWeek(sessions: Session[], start: Date) {
  const from = isoDate(start)
  const to = isoDate(addDays(start, 6))
  // A dated class also goes through onDate, so one cancelled or moved by the planner (in excludedDates) drops out.
  return sessions.filter((s) =>
    s.meeting.date ? s.meeting.date >= from && s.meeting.date <= to && onDate(s, new Date(`${s.meeting.date}T12:00`)) : onDate(s, addDays(start, (s.meeting.day - start.getDay() + 7) % 7)),
  )
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
