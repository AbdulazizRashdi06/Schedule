import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { ApiError, api, errorText, store } from '../lib/api'
import type { Me, Timetable } from '../lib/types'
import { buildSessions, type Session } from '../lib/slots'
import { Icon, cx } from '../components/ui'

export { Icon }

interface Data {
  me: Me
  tt: Timetable | null
}

// Shared by Today, Schedule and Course so moving between them doesn't reload or flash.
let cache: Data | null = null

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

async function load(): Promise<Data> {
  const me = await api.get<Me>('/me')
  const tt = me.setupDone ? await api.get<Timetable>('/me/timetable') : null
  return { me, tt }
}

export function useTimetable() {
  const [data, setData] = useState(cache)
  const [error, setError] = useState('')
  /** When set, we're offline and showing what was saved at this time. */
  const [offline, setOffline] = useState<Date | null>(null)

  const fetchNow = useCallback(async () => {
    try {
      const d = await load()
      cache = d
      save(d)
      setData(d)
      setOffline(null)
      setError('')
      return true
    } catch (e) {
      const old = e instanceof ApiError && e.status === 0 ? saved() : null
      if (old) {
        if (!cache) setData({ me: old.me, tt: old.tt })
        setOffline(new Date(old.savedAt))
      } else setError(errorText(e))
      return false
    }
  }, [])

  useEffect(() => {
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
    cache = { ...cache, tt: { ...cache.tt, classes: cache.tt.classes.map((c) => (c.section.id === sectionId ? { ...c, changed: false } : c)) } }
    setData(cache)
  }, [])

  return { me: data?.me ?? null, tt: data?.tt ?? null, sessions, error, markSeen, offline, retry: fetchNow }
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
