import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Me, Timetable } from '../lib/types'
import { buildSessions, type Session } from '../lib/slots'
import { cx } from '../components/ui'

// Shared by Today, Schedule and Course so moving between them doesn't reload or flash.
let cache: { me: Me; tt: Timetable | null } | null = null

export function useTimetable() {
  const [data, setData] = useState(cache)
  const [error, setError] = useState('')

  useEffect(() => {
    let live = true
    api
      .get<Me>('/me')
      .then(async (me) => {
        const tt = me.setupDone ? await api.get<Timetable>('/me/timetable') : null
        cache = { me, tt }
        if (live) setData(cache)
      })
      .catch((e) => live && setError(errorText(e)))
    return () => {
      live = false
    }
  }, [])

  const sessions = useMemo<Session[]>(() => (data?.tt ? buildSessions(data.tt) : []), [data])

  /** Clears the "Changed" badge once the student has looked at the class. */
  const markSeen = useCallback((sectionId: number) => {
    api.post('/me/seen', { sectionId }).catch(() => {})
    if (!cache?.tt) return
    cache = { ...cache, tt: { ...cache.tt, classes: cache.tt.classes.map((c) => (c.section.id === sectionId ? { ...c, changed: false } : c)) } }
    setData(cache)
  }, [])

  return { me: data?.me ?? null, tt: data?.tt ?? null, sessions, error, markSeen }
}

/** Forget the cached timetable (after the student edits their courses). */
export const clearTimetableCache = () => {
  cache = null
}

/** Dev only: `?now=2026-10-14T10:05` pins the clock, to check the live and between-classes states. */
const fakeNow = () => {
  const v = import.meta.env.DEV ? new URLSearchParams(location.search).get('now') : null
  return v ? new Date(v) : new Date()
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
  const location = useLocation()
  return () => (location.key !== 'default' ? navigate(-1) : navigate(fallback))
}

/** Lucide-style stroke icons. */
const PATHS = {
  back: 'M15 18l-6-6 6-6',
  prev: 'M15 18l-6-6 6-6',
  next: 'M9 18l6-6-6-6',
  arrow: 'M5 12h14M13 6l6 6-6 6',
  menu: 'M4 7h16M4 12h16M4 17h10',
  pin: 'M12 21s-7-6.2-7-11.5a7 7 0 0114 0C19 14.8 12 21 12 21zM12 12a2.5 2.5 0 100-5 2.5 2.5 0 000 5z',
  bell: 'M6 8a6 6 0 0112 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.94 1.94 0 003.4 0',
} as const

export function Icon({ name, className }: { name: keyof typeof PATHS; className?: string }) {
  return (
    <svg viewBox="0 0 24 24" className={cx('size-5 shrink-0', className)} fill="none" stroke="currentColor" strokeWidth={2.3} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={PATHS[name]} />
    </svg>
  )
}

/** 44px round icon button. */
export function RoundButton({ label, onClick, className, children }: { label: string; onClick: () => void; className?: string; children: ReactNode }) {
  return (
    <button type="button" aria-label={label} onClick={onClick} className={cx('grid size-11 shrink-0 place-items-center rounded-full transition active:scale-95', className)}>
      {children}
    </button>
  )
}

/** Shown when a picked section was removed or never chosen. */
export function NeedsSection({ tt, dark }: { tt: Timetable; dark?: boolean }) {
  if (tt.needsSection.length === 0) return null
  const removed = tt.needsSection.some((n) => n.removed)
  return (
    <Link
      to="/edit"
      className={cx('mb-5 flex items-center gap-3 rounded-[18px] px-4 py-3 text-[14px] font-semibold', dark ? 'bg-cream text-navy' : 'bg-navy text-cream')}
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
