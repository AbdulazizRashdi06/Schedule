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
  const text = keys.map((k) => `${DAY_SHORT[Number(k.split('|')[0])]} ${k.split('|')[1]}`).join(sep)
  if (!dated || keys.length === count.size) return text
  return text ? `${text}${sep}changes by week` : 'Changes by week'
}

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
