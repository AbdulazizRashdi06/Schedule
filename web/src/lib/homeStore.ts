import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { ApiError, api, errorText, store } from './api'
import type { Me, TeachMe, Timetable, Weeks } from './types'
import { campusTime, type Session } from './slots'

/** What the home page draws from: a student's own timetable, or the classes a teacher teaches. */
export interface HomeData {
  /** The student, on the student side. */
  me: Me | null
  /** Who is using the teacher side, on the teacher side. */
  teach: TeachMe | null
  /** The semester's teaching weeks, from whichever of the two is set. */
  weeks?: Weeks
  tt: Timetable | null
  /** Everything that is really on. */
  sessions: Session[]
  /** Cancelled classes and the old halves of moved ones: drawn greyed in their place, never counted. */
  ghosts: Session[]
  error: string
  /** Clears the "Changed" badge once the class has been looked at. */
  markSeen: (sectionId: number) => void
  /** When set, we're offline and showing what was saved at this time (campus time). */
  offline: Date | null
  retry: () => Promise<boolean>
}

interface Data {
  me: Me | null
  teach: TeachMe | null
  tt: Timetable | null
}

/** A copy kept for offline, stamped with whose it is (`who`: the session's kind and name). */
type Saved = Data & { who?: string; savedAt: string }

const FRESH_MS = 5_000
/** Coming back to the tab after this long asks again, so a class cancelled meanwhile shows without a reload. */
const STALE_MS = 60_000
const NO_GHOSTS: Session[] = []

/**
 * The signed-in person, as lib/session wrote it after the server's last answer ("student:24-0161"). Null before the
 * first answer under this build.
 */
const who = () => store.get('who')

/**
 * One home's data: the timetable in memory (shared by every screen showing it), a copy saved for offline, and the
 * loading of both. The student home and the teacher home are two separate instances that share nothing, so one can
 * never show in the other.
 */
export function createHomeStore(cfg: {
  /** The localStorage key of the offline copy. */
  savedKey: string
  fetch: () => Promise<Data>
  /** Where "I've seen this class's change" is posted. */
  seenUrl: string
  build: (tt: Timetable) => Session[]
  /** false when nothing may be kept on the device (an owner viewing as a teacher). */
  canSave: () => boolean
}) {
  // Shared by every screen so moving between them doesn't reload or flash. Screens on at the same time (home and the
  // Today inside it) all show it, so a change one of them loads or makes shows in every one.
  let cache: Data | null = null
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
  const save = (d: Data) => {
    if (!cfg.canSave()) return
    const copy: Saved = { ...d, who: who() ?? undefined, savedAt: new Date().toISOString() }
    store.set(cfg.savedKey, JSON.stringify(copy))
  }
  /**
   * The saved copy, only when it is this person's. A copy with no owner was saved before copies had one, when only
   * students had a home: it counts for a student and for nobody else.
   */
  function saved(): Saved | null {
    if (!cfg.canSave()) return null
    try {
      const copy = JSON.parse(store.get(cfg.savedKey) ?? 'null') as Saved | null
      if (!copy) return null
      const now = who()
      const mine = copy.who ? copy.who === now : now === null || now.startsWith('student:')
      return mine ? copy : null
    } catch {
      return null
    }
  }

  // Screens that mount together (home and the Today inside it) share one request instead of each sending their own,
  // and a screen that mounts just after a load (the Today home draws once the data is in) uses it rather than asking again.
  let inflight: Promise<Data> | null = null
  let loadedAt = 0
  /** Goes up on sign-out, so a reply asked for by the signed-out person is thrown away rather than shown to the next. */
  let generation = 0
  function load(): Promise<Data> {
    if (inflight) return inflight
    const asked = generation
    const request: Promise<Data> = cfg
      .fetch()
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

  function useData(): HomeData {
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
        if (old) share(cache ?? { me: old.me ?? null, teach: old.teach ?? null, tt: old.tt }, campusTime(new Date(old.savedAt)))
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

    // Back on the tab after a while: ask again, so a class a teacher cancelled meanwhile shows without a reload.
    useEffect(() => {
      const seen = () => {
        if (document.visibilityState === 'visible' && cache && Date.now() - loadedAt > STALE_MS) void fetchNow()
      }
      document.addEventListener('visibilitychange', seen)
      return () => document.removeEventListener('visibilitychange', seen)
    }, [fetchNow])

    const sessions = useMemo<Session[]>(() => (data?.tt ? cfg.build(data.tt) : []), [data])

    const markSeen = useCallback((sectionId: number) => {
      api.post(cfg.seenUrl, { sectionId }).catch(() => {})
      if (!cache?.tt) return
      share({ ...cache, tt: { ...cache.tt, classes: cache.tt.classes.map((c) => (c.section.id === sectionId ? { ...c, changed: false } : c)) } }, offlineAt)
    }, [])

    return {
      me: data?.me ?? null,
      teach: data?.teach ?? null,
      weeks: data?.me?.semester?.weeks ?? data?.teach?.semester?.weeks,
      tt: data?.tt ?? null,
      sessions,
      ghosts: NO_GHOSTS,
      error,
      markSeen,
      offline,
      retry: fetchNow,
    }
  }

  return {
    useData,
    /** Forget the timetable in memory (after an edit elsewhere), so the next screen loads it again. */
    clear: () => {
      cache = null
    },
    /** On sign-out: forget everything about this person, including a load still on its way. */
    reset: () => {
      cache = null
      offlineAt = null
      inflight = null
      loadedAt = 0
      generation++
    },
  }
}
