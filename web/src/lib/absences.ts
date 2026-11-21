import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { addDays, isoDate, nowMinutes, onDate, type Session } from './slots'
import type { Weeks } from './types'

export interface Absence {
  courseId: number
  date: string
  start: string
}

interface Reply {
  limit: number | null
  absences: Absence[]
}

/** Every page showing absences reads the same list, so marking one on a session updates the course page too. */
let shared: Reply | null = null
const listeners = new Set<(r: Reply) => void>()
/**
 * Goes up on sign-out, and as each mark starts and finishes saving. A list asked for before it went up may have been
 * read before that change, so it's thrown away rather than undoing the mark (or kept for the next student).
 */
let version = 0
/** Marks still saving. */
let saving = 0
const publish = (r: Reply) => {
  shared = r
  listeners.forEach((l) => l(r))
}
/** Asks for the list and shows it, unless it changed while the request was on its way. */
function refresh() {
  const asked = version
  api.get<Reply>('/me/absences').then((r) => {
    if (asked === version) publish(r)
  }, () => {})
}

/** Forgets the signed-out student's list, so whoever signs in next doesn't see it while theirs loads. */
export function resetAbsences() {
  shared = null
  version++
}

/** The student's missed classes and the attendance limit (if the university set one), kept in step across pages. */
export function useAbsences() {
  const [reply, setReply] = useState<Reply | null>(shared)
  useEffect(() => {
    listeners.add(setReply)
    refresh()
    return () => {
      listeners.delete(setReply)
    }
  }, [])

  /** Marks a session missed or not; shown at once, put back if saving fails. */
  const mark = useCallback(async (courseId: number, date: Date, start: string, missed: boolean) => {
    const before = shared ?? { limit: null, absences: [] }
    const key = (a: Absence) => a.courseId === courseId && a.date === isoDate(date) && a.start === start
    version++
    saving++
    publish({ ...before, absences: missed ? [...before.absences.filter((a) => !key(a)), { courseId, date: isoDate(date), start }] : before.absences.filter((a) => !key(a)) })
    try {
      await api.put('/me/absences', { courseId, date: isoDate(date), start, missed })
    } catch (e) {
      publish(before)
      throw e
    } finally {
      // A list dropped while this was saving may have had newer marks (from another device): ask again once all are saved.
      version++
      if (--saving === 0) refresh()
    }
  }, [])

  return { limit: reply?.limit ?? null, absences: reply?.absences ?? [], ready: reply !== null, mark }
}

/** Whether a class's sessions count towards attendance: the student's own slots and exams don't. */
export const counts = (s: Session) => !s.mine && s.type !== 'exam'

/**
 * How many sessions of a course have been held so far this semester, from week 1: those that have started, as a class
 * can be marked missed once it's on (so "Missed 1 of 1", not "of 0"). Null when the semester's start isn't known, so
 * there's nothing to count from.
 */
export function heldSoFar(courseId: number, all: Session[], weeks: Weeks | undefined, now: Date) {
  if (!weeks?.week1Start) return null
  const [y, m, d] = weeks.week1Start.split('-').map(Number)
  const today = isoDate(now)
  const nowMin = nowMinutes(now)
  const mine = all.filter((s) => s.cls.courseId === courseId && counts(s))
  let held = 0
  for (let day = new Date(y, m - 1, d); isoDate(day) <= today; day = addDays(day, 1)) {
    const iso = isoDate(day)
    for (const s of mine) if (onDate(s, day) && (iso < today || s.start <= nowMin)) held++
  }
  return held
}
