import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { ApiError, api, store } from './api'
import { resetAbsences } from './absences'
import { deviceSubscription, resetNotifySettings } from './push'
import type { SessionInfo } from './types'
import { viewAs } from './viewAs'
import { resetTimetable } from '../student/timetable'
import { resetTeach } from '../teacher/teachData'

interface SessionCtx {
  session: SessionInfo | null
  refresh: () => Promise<SessionInfo>
  logout: () => Promise<void>
}

const Ctx = createContext<SessionCtx>(null!)

/** Kept so a student who opens the app offline still gets their saved timetable instead of the sign-in screen. */
const SAVED = 'session'
/**
 * Whose things are kept on this device: the kind and name of the last session the server answered with
 * ("student:24-0161", "teacher:raed.bolbol@gutech.edu.om"). The saved timetables carry the same stamp.
 */
const WHO = 'who'
const whoIs = (s: SessionInfo) => (s.kind ? `${s.kind}:${s.name ?? ''}` : null)

/** Everything one person left in this browser: their saved timetables and whatever of theirs is in memory. */
function forgetPerson() {
  store.remove('timetable')
  store.remove('timetable:teach')
  resetTimetable()
  resetTeach()
  resetAbsences()
  resetNotifySettings()
  viewAs.clear()
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionInfo | null>(null)
  const refresh = useCallback(async () => {
    try {
      const s = await api.get<SessionInfo>('/auth/session')
      // One device, two people: when the server says somebody else is signed in than last time (or nobody), nothing
      // of the last person's stays. A browser with no stamp yet was last used by a student on the build before
      // teachers: their saved timetable is kept for them, and dropped for anyone else.
      const who = whoIs(s)
      const last = store.get(WHO)
      if (last ? last !== who : s.kind !== 'student') forgetPerson()
      if (who) store.set(WHO, who)
      else store.remove(WHO)
      store.set(SAVED, JSON.stringify(s))
      setSession(s)
      return s
    } catch (e) {
      // A signed-out answer is a 200 with no kind, so a failed request never means signed out: offline (0) or the
      // server restarting (5xx) keeps the saved session.
      const failed = e instanceof ApiError && (e.status === 0 || e.status >= 500)
      const saved = failed ? store.get(SAVED) : null
      const s: SessionInfo = saved ? JSON.parse(saved) : { kind: null }
      setSession(s)
      return s
    }
  }, [])
  const logout = useCallback(async () => {
    // The server drops this device's notifications in the same step as the sign-in, so the next person to use it
    // doesn't see this student's reminders.
    const sub = await deviceSubscription().catch(() => null)
    // Only the server's answer clears the sign-in cookie: if it doesn't come (offline, server restarting), this throws
    // and the student stays signed in here too, with notifications still on, rather than being told they're out while
    // the cookie still works.
    await api.post('/auth/logout', sub ? { endpoint: sub.endpoint } : {})
    await sub?.unsubscribe().catch(() => {})
    store.remove(SAVED)
    store.remove(WHO)
    forgetPerson()
    setSession({ kind: null })
  }, [])
  useEffect(() => {
    refresh()
  }, [refresh])
  return <Ctx.Provider value={{ session, refresh, logout }}>{children}</Ctx.Provider>
}

export const useSession = () => useContext(Ctx)
