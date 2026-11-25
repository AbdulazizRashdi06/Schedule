import type { Meeting, OfficeHour, Timetable, TimetableClass } from '../lib/types'
import { minutes } from '../lib/time'
import { blockOn, buildSessions, markClashes, slotSpan, sortSessions, type Session } from '../lib/slots'

/** Office hours dressed as a class so every view can show them: ids far below any own slot's, so nothing mistakes one for a class. */
const OFFICE_ID = -1_000_000_000
function officeClass(h: OfficeHour, meeting: Meeting): TimetableClass {
  const id = OFFICE_ID - h.id
  return {
    courseId: id, code: 'Office hours', name: 'Office hours', type: 'Core', credits: null, changed: false,
    section: { id, number: '', instructor: '', meetings: [meeting], changedAt: null, changeSummary: null },
  }
}

/** A session that is a class on the timetable (not an own slot, office hours or a rep's event). */
const isClass = (s: Session) => !s.mine && !s.office && !s.plannerEventId

/**
 * Whether sessions sharing a merge key are one real class. A key the university's listing gives (same time and room)
 * only joins different courses: two sections of one course at one time stay two blocks and show as a clash. A key a
 * teacher's change gives ("g:…") joins every section of that change.
 */
const joins = (key: string, a: Session, b: Session) => key.startsWith('g:') || a.cls.courseId !== b.cls.courseId

/**
 * A teacher's sessions: their classes with one real class under several course codes collapsed into one block, their
 * own slots, the reps' events and their office hours.
 */
export function buildTeachSessions(tt: Timetable): Session[] {
  // No clash marks yet: BE1101, IBSM1101 and LOG1101 at one time are different courses, and would be marked as
  // clashing with each other before they are collapsed.
  const all = buildSessions(tt, { clashes: false })

  for (const h of tt.officeHours ?? []) {
    const meeting: Meeting = { day: h.day, start: h.start, end: h.end, room: h.room, date: null, excludedDates: h.excludedDates ?? null }
    const start = minutes(h.start)
    const end = minutes(h.end)
    all.push({
      cls: officeClass(h, meeting), meeting, type: 'office', ...slotSpan(start, end), start, end, clash: false, clashDates: [], notes: 0, noteDates: {}, office: h,
      // Weekly, so only in the teaching weeks.
      ...(tt.span && { firstDate: tt.span.first, lastDate: tt.span.last }),
    })
  }

  // One real class, several codes: the sessions that share a merge key become one block.
  const byKey = new Map<string, Session[]>()
  for (const s of all) {
    const key = isClass(s) ? s.meeting.merge : undefined
    if (key) byKey.set(key, [...(byKey.get(key) ?? []), s])
  }
  const gone = new Set<Session>()
  const blocks: Session[] = []
  for (const [key, group] of byKey) {
    // The lead is the first by course code, as the server picks it for reminders.
    const sorted = [...group].sort((a, b) => (a.cls.code < b.cls.code ? -1 : a.cls.code > b.cls.code ? 1 : a.cls.section.id - b.cls.section.id))
    const lead = sorted[0]
    const members = sorted.filter((s, i) => i === 0 || sorted.slice(0, i).every((m) => joins(key, m, s)))
    if (members.length < 2) continue
    // Each member keeps its own skipped dates; the block is hidden on a date only when none of them is on.
    const skipped = members.map((m) => m.meeting.excludedDates ?? [])
    const excludedDates = skipped[0].filter((d) => skipped.every((list) => list.includes(d)))
    members.forEach((m) => gone.add(m))
    blocks.push({
      ...lead,
      cls: { ...lead.cls, changed: members.some((m) => m.cls.changed) },
      meeting: { ...lead.meeting, excludedDates },
      notes: 0,
      merged: members,
    })
  }
  const out = [...all.filter((s) => !gone.has(s)), ...blocks]

  // A teacher can't clash with themselves in one section, nor with their office hours. What is left after the
  // collapse with the same key is two sections of one course at one time, which is a clash.
  markClashes(out, (a, b) => !!a.office || !!b.office || a.cls.section.id === b.cls.section.id)
  return sortSessions(out)
}

/**
 * How many Classy users a class has on a date: the section's own number, or for a merged class everyone in it counted
 * once (from the server) when all its codes meet, else the sum of the codes still on.
 */
export function blockSize(s: Session, date: Date | undefined, tt: Timetable | null): number | null {
  if (!s.merged) return s.cls.students ?? null
  const on = blockOn(s, date)
  const all = s.meeting.merge ? tt?.mergeSizes?.[s.meeting.merge] : undefined
  if (on.length === s.merged.length && all != null) return all
  return on.reduce((n, m) => n + (m.cls.students ?? 0), 0)
}
