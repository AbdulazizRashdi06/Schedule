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

/** Forgets the signed-out student's list, so whoever signs in next doesn't see it while theirs loads. */
export function resetAbsences() {
  shared = null
  generation++
}
/** Goes up on sign-out, so a list that arrives after it isn't kept for the next student. */
let generation = 0
/** The last save asked for on each session ("courseId.date.start"), so the next one waits for it. */
const saving = new Map<string, Promise<unknown>>()

/** The student's missed classes and the attendance limit (if the university set one), kept in step across pages. */
export function useAbsences() {
  const [reply, setReply] = useState<Reply | null>(shared)
  useEffect(() => {
    listeners.add(setReply)
    const asked = generation
    api.get<Reply>('/me/absences').then((r) => {
      if (asked === generation) publish(r)
    }, () => {})
    return () => {
      listeners.delete(setReply)
    }
  }, [])

  /**
   * Marks a session missed or not; shown at once, put back if saving fails. Taps on one session are saved one after
   * another, so a quick undo can't reach the server before the tap it undoes.
   */
  const mark = useCallback(async (courseId: number, date: Date, start: string, missed: boolean) => {
    const before = shared ?? { limit: null, absences: [] }
    const id = `${courseId}.${isoDate(date)}.${start}`
    const key = (a: Absence) => a.courseId === courseId && a.date === isoDate(date) && a.start === start
    const was = before.absences.some(key)
    const set = (on: boolean) => {
      const now = shared ?? before
      publish({ ...now, absences: on ? [...now.absences.filter((a) => !key(a)), { courseId, date: isoDate(date), start }] : now.absences.filter((a) => !key(a)) })
    }
    set(missed)
    const run = (saving.get(id) ?? Promise.resolve())
      .catch(() => {})
      .then(() => api.put('/me/absences', { courseId, date: isoDate(date), start, missed }))
    saving.set(id, run)
    try {
      await run
    } catch (e) {
      // Only undone if no later tap on this session has replaced it; that one is saved next.
      if (saving.get(id) === run) set(was)
      throw e
    } finally {
      if (saving.get(id) === run) saving.delete(id)
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
