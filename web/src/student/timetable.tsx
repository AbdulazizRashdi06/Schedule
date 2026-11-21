import { useCallback, useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ApiError, api, errorText, store } from '../lib/api'
import type { Me, Timetable } from '../lib/types'
import { buildSessions, campusNow, campusTime, type Session } from '../lib/slots'
import { Icon, cx } from '../components/ui'

export { Icon }

interface Data {
  me: Me
  tt: Timetable | null
}

// Shared by Today, Schedule and Course so moving between them doesn't reload or flash. Screens on at the same time
// (home and the Today inside it) all show it, so a change one of them loads or makes shows in every one.
let cache: Data | null = null
/** When set, we're offline and showing what was saved at this time (campus time). Shared like the timetable. */
let offlineAt: Date | null = null
const listeners = new Set<() => void>()
function share(d: Data | null, offline: Date | null) {
  cache = d
  offlineAt = offline
  listeners.forEach((l) => l())
}
const subscribe = (l: () => void) => {
  listeners.add(l)
  return () => {
    listeners.delete(l)
  }
}

/** The last timetable that loaded, shown (with a banner) when the server can't be reached. */
const SAVED = 'timetable'
const save = (d: Data) => store.set(SAVED, JSON.stringify({ ...d, savedAt: new Date().toISOString() }))
function saved(): (Data & { savedAt: string }) | null {
  try {
    return JSON.parse(store.get(SAVED) ?? 'null')
  } catch {
    return null
  }
}

async function fetchData(): Promise<Data> {
  const me = await api.get<Me>('/me')
  const tt = me.setupDone ? await api.get<Timetable>('/me/timetable') : null
  return { me, tt }
}

// Screens that mount together (home and the Today inside it) share one request instead of each sending their own,
// and a screen that mounts just after a load (the Today home draws once the data is in) uses it rather than asking again.
let inflight: Promise<Data> | null = null
let loadedAt = 0
const FRESH_MS = 5_000
/** Goes up on sign-out, so a reply asked for by the signed-out student is thrown away rather than shown to the next. */
let generation = 0
function load(): Promise<Data> {
  if (inflight) return inflight
  const asked = generation
  const request: Promise<Data> = fetchData()
    .then((d) => {
      if (asked !== generation) throw new Error('Signed out')
      loadedAt = Date.now()
      return d
    })
    .finally(() => {
      if (inflight === request) inflight = null
    })
  inflight = request
  return request
}

export function useTimetable() {
  const data = useSyncExternalStore(subscribe, () => cache)
  const offline = useSyncExternalStore(subscribe, () => offlineAt)
  const [error, setError] = useState('')

  const fetchNow = useCallback(async () => {
    try {
      const d = await load()
      save(d)
      share(d, null)
      setError('')
      return true
    } catch (e) {
      // As in the session: offline (0) or the server restarting (5xx) shows the saved timetable.
      const old = e instanceof ApiError && (e.status === 0 || e.status >= 500) ? saved() : null
      if (old) share(cache ?? { me: old.me, tt: old.tt }, campusTime(new Date(old.savedAt)))
      else setError(errorText(e))
      return false
    }
  }, [])

  useEffect(() => {
    if (cache && Date.now() - loadedAt < FRESH_MS) return
    void fetchNow()
  }, [fetchNow])

  // Back online: try again without waiting for a tap.
  useEffect(() => {
    if (!offline) return
    const on = () => void fetchNow()
    window.addEventListener('online', on)
    return () => window.removeEventListener('online', on)
  }, [offline, fetchNow])

  const sessions = useMemo<Session[]>(() => (data?.tt ? buildSessions(data.tt) : []), [data])

  /** Clears the "Changed" badge once the student has looked at the class. */
  const markSeen = useCallback((sectionId: number) => {
    api.post('/me/seen', { sectionId }).catch(() => {})
    if (!cache?.tt) return
    share({ ...cache, tt: { ...cache.tt, classes: cache.tt.classes.map((c) => (c.section.id === sectionId ? { ...c, changed: false } : c)) } }, offlineAt)
  }, [])

  return { me: data?.me ?? null, tt: data?.tt ?? null, sessions, error, markSeen, offline, retry: fetchNow }
}

/** Forget the cached timetable (after the student edits their courses). */
export const clearTimetableCache = () => {
  cache = null
}

/** On sign-out: forget everything about this student, including a load still on its way. */
export const resetTimetable = () => {
  cache = null
  offlineAt = null
  inflight = null
  loadedAt = 0
  generation++
}

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
