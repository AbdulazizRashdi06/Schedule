import type { Meeting, Section } from './types'

export const DAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
export const DAY_LONG = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

export const minutes = (hhmm: string) => {
  const [h, m] = hhmm.split(':').map(Number)
  return h * 60 + m
}

/** "13:30" → "1:30 PM" */
export function fmtTime(hhmm: string) {
  const [h, m] = hhmm.split(':').map(Number)
  const suffix = h >= 12 ? 'PM' : 'AM'
  const h12 = h % 12 === 0 ? 12 : h % 12
  return `${h12}:${String(m).padStart(2, '0')} ${suffix}`
}

export const fmtRange = (m: { start: string; end: string }) => `${fmtTime(m.start)} – ${fmtTime(m.end)}`

export const meetingsOverlap = (a: Meeting, b: Meeting) =>
  a.day === b.day && minutes(a.start) < minutes(b.end) && minutes(b.start) < minutes(a.end)

export const sectionsClash = (a: Section, b: Section) => a.meetings.some((x) => b.meetings.some((y) => meetingsOverlap(x, y)))

/** "Sun Tue 8:00 AM" style: groups meetings that share a time. */
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
      return compact ? `${d} ${fmtTime(start)}` : `${d} ${fmtTime(start)} – ${fmtTime(end)}`
    })
    .join(' · ')
}

export const rooms = (s: Section) => [...new Set(s.meetings.map((m) => m.room).filter(Boolean))].join(', ')
