import type { Meeting, Timetable, TimetableClass } from './types'
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

export type ClassType = 'lecture' | 'lab' | 'tutorial'

export interface Session {
  cls: TimetableClass
  meeting: Meeting
  type: ClassType
  /** Index into SLOTS. */
  slot: number
  /** Minutes since midnight. */
  start: number
  end: number
  clash: boolean
}

/** The upload has no session type, so it comes from the room: "Lab L2" is a lab, "Tut 3" a tutorial, anything else a lecture. */
export function sessionType(room: string): ClassType {
  if (/\blab/i.test(room)) return 'lab'
  if (/\btut/i.test(room)) return 'tutorial'
  return 'lecture'
}

export const TYPE_NAME: Record<ClassType, string> = { lecture: 'Lecture', lab: 'Lab', tutorial: 'Tutorial' }
/** Block colours: lecture cream, lab ice, tutorial sky. Text on every block is navy. */
export const TYPE_BG: Record<ClassType, string> = { lecture: 'bg-cream', lab: 'bg-ice', tutorial: 'bg-sky' }

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

export function buildSessions(tt: Timetable): Session[] {
  const out: Session[] = []
  for (const cls of tt.classes)
    for (const meeting of cls.section.meetings) {
      const start = minutes(meeting.start)
      const end = minutes(meeting.end)
      out.push({ cls, meeting, type: sessionType(meeting.room), slot: slotOf(start, end), start, end, clash: false })
    }
  for (const a of out)
    a.clash = out.some((b) => b !== a && b.cls.courseId !== a.cls.courseId && b.meeting.day === a.meeting.day && a.start < b.end && b.start < a.end)
  return out.sort((a, b) => a.meeting.day - b.meeting.day || a.start - b.start)
}

/** Sunday to Thursday, plus Friday or Saturday only when a class meets then. */
export const weekDays = (sessions: Session[]) => [0, 1, 2, 3, 4, 5, 6].filter((d) => d <= 4 || sessions.some((s) => s.meeting.day === d))

export const nowMinutes = (now: Date) => now.getHours() * 60 + now.getMinutes()

/** The Sunday that starts the week `offset` weeks from now. */
export function weekStart(now: Date, offset = 0) {
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - now.getDay() + offset * 7)
  return d
}

export const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
/** "11 Oct" */
export const fmtDate = (d: Date) => `${d.getDate()} ${MONTHS[d.getMonth()]}`

/** "11 – 15 Oct", or "29 Sep – 3 Oct" across months. */
export function fmtWeekRange(start: Date, days: number) {
  const end = addDays(start, days - 1)
  return start.getMonth() === end.getMonth() ? `${start.getDate()} – ${fmtDate(end)}` : `${fmtDate(start)} – ${fmtDate(end)}`
}

export function weekTitle(offset: number) {
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
