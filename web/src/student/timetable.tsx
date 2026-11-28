import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../lib/api'
import type { Me, Timetable } from '../lib/types'
import { buildGhosts, buildSessions, campusNow } from '../lib/slots'
import { createHomeStore } from '../lib/homeStore'
import { timeParts } from '../lib/time'
import { Icon, cx } from '../components/ui'

export { Icon }

// The student's timetable: shared by Today, the week and Course so moving between them doesn't reload or flash, with a
// copy saved for offline. The store itself is lib/homeStore; the teacher side has its own instance (teacher/teachData).
const home = createHomeStore({
  savedKey: 'timetable',
  fetch: async () => {
    const me = await api.get<Me>('/me')
    const tt = me.setupDone ? await api.get<Timetable>('/me/timetable') : null
    return { me, teach: null, tt }
  },
  seenUrl: '/me/seen',
  build: (tt) => buildSessions(tt),
  ghosts: buildGhosts,
  canSave: () => true,
})

export const useTimetable = home.useData

/** Forget the cached timetable (after the student edits their courses). */
export const clearTimetableCache = home.clear

/** On sign-out: forget everything about this student, including a load still on its way. */
export const resetTimetable = home.reset

/**
 * The time on campus (see campusNow), whatever the device's time zone. Dev only: `?now=2026-10-14T10:05` (an Oman
 * time) pins the clock, to check the live and between-classes states.
 */
const fakeNow = () => {
  const v = import.meta.env.DEV ? new URLSearchParams(location.search).get('now') : null
  return v ? new Date(v) : campusNow()
}

export function useNow() {
  const [now, setNow] = useState(fakeNow)
  useEffect(() => {
    const t = setInterval(() => setNow(fakeNow()), 30_000)
    return () => clearInterval(t)
  }, [])
  return now
}

/** Back to where the student came from, or to `fallback` when the page was opened directly. */
export function useBack(fallback = '/') {
  const navigate = useNavigate()
  // React Router numbers its history entries in history.state.idx. A replace (the invite flow, the week arrows) keeps
  // the number, so only idx > 0 means there is a Classy page to go back to; otherwise go to the fallback in place.
  return () => ((window.history.state as { idx?: number } | null)?.idx ? navigate(-1) : navigate(fallback, { replace: true }))
}

/** A class time in display type: the clock big, and AM/PM small beside it (under it with `stack`, where the column is narrow). */
export function TimeText({ t, stack }: { t: string; stack?: boolean }) {
  const [clock, suffix] = timeParts(t)
  if (!suffix) return <>{clock}</>
  return (
    <>
      {clock}
      <span className={cx('font-mono font-bold tracking-[0.04em] no-underline', stack ? 'mt-1 block text-[11px] leading-none' : 'ml-[0.1em] text-[0.26em]')}>{suffix}</span>
    </>
  )
}

/** 48px round icon button. */
export function RoundButton({ label, onClick, className, children }: { label: string; onClick: () => void; className?: string; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} onClick={onClick} className={cx('press grid size-12 shrink-0 place-items-center rounded-full', className)}>
      {children}
    </button>
  )
}

/** The back button on cream screens: a navy ring. */
export function BackButton({ label = 'Back', onClick }: { label?: string; onClick: () => void }) {
  return (
    <RoundButton label={label} onClick={onClick} className="border-[2.5px] border-navy text-navy">
      <Icon name="back" />
    </RoundButton>
  )
}

/** Shown when a picked section was removed or never chosen. */
export function NeedsSection({ tt, dark }: { tt: Timetable; dark?: boolean }) {
  if (tt.needsSection.length === 0) return null
  const removed = tt.needsSection.some((n) => n.removed)
  return (
    <Link
      to="/edit"
      className={cx('press mb-5 flex items-center gap-3 rounded-[18px] px-4 py-3 text-[14px] font-semibold', dark ? 'bg-cream text-navy' : 'bg-navy text-cream')}
    >
      <span className="min-w-0 flex-1">
        {removed ? 'A section you picked was removed: ' : 'No section picked for '}
        {tt.needsSection.map((n) => n.code).join(', ')}
      </span>
      <span className="label shrink-0 text-[10px]">Pick again</span>
      <Icon name="arrow" className="size-4" />
    </Link>
  )
}
