import { prefs } from './prefs'
import type { Meeting, Section } from './types'

export const DAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
export const DAY_LONG = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

export const minutes = (hhmm: string) => {
  const [h, m] = hhmm.split(':').map(Number)
  return h * 60 + m
}

/** A time split for display: the clock part, and "AM"/"PM" when the student reads 12-hour times (Menu → Settings). */
export function timeParts(hhmm: string): [clock: string, suffix: string] {
  if (prefs.timeFormat === '24') return [hhmm, '']
  const [h, m] = hhmm.split(':').map(Number)
  if (Number.isNaN(h) || Number.isNaN(m)) return [hhmm, '']
  return [`${h % 12 === 0 ? 12 : h % 12}:${String(m).padStart(2, '0')}`, h >= 12 ? 'PM' : 'AM']
}

/** "13:30" as the student reads it: "13:30", or "1:30 PM". Every class time on screen goes through here. */
export function fmtTime(hhmm: string) {
  const [clock, suffix] = timeParts(hhmm)
  return suffix ? `${clock} ${suffix}` : clock
}

/** Without AM/PM, for tight spots where the day around it makes it clear: "13:30", or "1:30". */
export const fmtShort = (hhmm: string) => timeParts(hhmm)[0]

/** "13:15–14:45", or "1:15–2:45 PM" (AM/PM once when both share it: "11:30 AM–1:00 PM" otherwise). */
export function fmtRange(start: string, end: string) {
  const [a, sa] = timeParts(start)
  const [b, sb] = timeParts(end)
  if (!sb) return `${a}–${b}`
  return sa === sb ? `${a}–${b} ${sb}` : `${a} ${sa}–${b} ${sb}`
}

/** Same weekday and times overlap; two dated classes must also share the date. */
export const meetingsOverlap = (a: Meeting, b: Meeting) =>
  a.day === b.day && minutes(a.start) < minutes(b.end) && minutes(b.start) < minutes(a.end) && (!a.date || !b.date || a.date === b.date)

/** "Sun 08:00 · Tue 11:30". Dated classes fold into their usual weekly times, plus "changes by week" when some differ. */
export function meetsText(s: Section, sep = '  ·  ') {
  const count = new Map<string, number>()
  for (const m of s.meetings) count.set(`${m.day}|${m.start}`, (count.get(`${m.day}|${m.start}`) ?? 0) + 1)
  const dated = s.meetings.some((m) => m.date)
  const keys = [...count.entries()]
    .filter(([, n]) => !dated || n >= 2)
    .map(([k]) => k)
    .sort((a, b) => Number(a.split('|')[0]) - Number(b.split('|')[0]) || a.localeCompare(b))
  const text = keys.map((k) => `${DAY_SHORT[Number(k.split('|')[0])]} ${fmtTime(k.split('|')[1])}`).join(sep)
  if (!dated || keys.length === count.size) return text
  return text ? `${text}${sep}changes by week` : 'Changes by week'
}

export const sectionsClash = (a: Section, b: Section) => a.meetings.some((x) => b.meetings.some((y) => meetingsOverlap(x, y)))

/** "Sun Tue 08:00" style: groups meetings that share a time. */
export function sectionSummary(s: Section, compact = false): string {
  const groups = new Map<string, number[]>()
  for (const m of s.meetings) {
    const key = `${m.start}-${m.end}`
    groups.set(key, [...(groups.get(key) ?? []), m.day])
  }
  return [...groups.entries()]
    .map(([key, days]) => {
      const [start, end] = key.split('-')
      const d = days.sort().map((x) => DAY_SHORT[x]).join(' ')
      return compact ? `${d} ${fmtTime(start)}` : `${d} ${fmtRange(start, end)}`
    })
    .join(' · ')
}

/** Classes in a usual week: dated classes are averaged over the weeks they run in. */
export function classesPerWeek(s: Section) {
  const dated = s.meetings.filter((m) => m.date)
  if (dated.length === 0) return s.meetings.length
  // Week number counted from a Sunday (1 Jan 1970 was a Thursday).
  const weeks = new Set(dated.map((m) => Math.floor((Date.parse(m.date + 'T00:00:00Z') / 86_400_000 + 4) / 7)))
  return Math.round(dated.length / weeks.size) + (s.meetings.length - dated.length)
}

export const rooms = (s: Section) => [...new Set(s.meetings.map((m) => m.room).filter(Boolean))].join(', ')
