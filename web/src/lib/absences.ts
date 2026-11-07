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
const publish = (r: Reply) => {
  shared = r
  listeners.forEach((l) => l(r))
}

/** The student's missed classes and the attendance limit (if the university set one), kept in step across pages. */
export function useAbsences() {
  const [reply, setReply] = useState<Reply | null>(shared)
  useEffect(() => {
    listeners.add(setReply)
    api.get<Reply>('/me/absences').then(publish, () => {})
    return () => {
      listeners.delete(setReply)
    }
  }, [])

  /** Marks a session missed or not; shown at once, put back if saving fails. */
  const mark = useCallback(async (courseId: number, date: Date, start: string, missed: boolean) => {
    const before = shared ?? { limit: null, absences: [] }
    const key = (a: Absence) => a.courseId === courseId && a.date === isoDate(date) && a.start === start
    publish({ ...before, absences: missed ? [...before.absences.filter((a) => !key(a)), { courseId, date: isoDate(date), start }] : before.absences.filter((a) => !key(a)) })
    try {
      await api.put('/me/absences', { courseId, date: isoDate(date), start, missed })
    } catch (e) {
      publish(before)
      throw e
    }
  }, [])

  return { limit: reply?.limit ?? null, absences: reply?.absences ?? [], ready: reply !== null, mark }
}

/** Whether a class's sessions count towards attendance: the student's own slots and exams don't. */
export const counts = (s: Session) => !s.mine && s.type !== 'exam'

/**
 * How many sessions of a course have been held so far this semester (finished ones only), from week 1. Null when the
 * semester's start isn't known, so there's nothing to count from.
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
    for (const s of mine) if (onDate(s, day) && (iso < today || s.end <= nowMin)) held++
  }
  return held
}
