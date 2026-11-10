import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { ApiError, api, store } from './api'
import { resetAbsences } from './absences'
import { disableDevice, resetNotifySettings } from './push'
import type { SessionInfo } from './types'
import { resetTimetable } from '../student/timetable'

interface SessionCtx {
  session: SessionInfo | null
  refresh: () => Promise<SessionInfo>
  logout: () => Promise<void>
}

const Ctx = createContext<SessionCtx>(null!)

/** Kept so a student who opens the app offline still gets their saved timetable instead of the sign-in screen. */
const SAVED = 'session'

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionInfo | null>(null)
  const refresh = useCallback(async () => {
    try {
      const s = await api.get<SessionInfo>('/auth/session')
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
    // First, while still signed in: this device stops getting this student's reminders, so the next person to use it
    // doesn't see them.
    await disableDevice().catch(() => {})
    await api.post('/auth/logout').catch(() => {})
    store.remove(SAVED)
    store.remove('timetable')
    resetAbsences()
    resetNotifySettings()
    resetTimetable()
    setSession({ kind: null })
  }, [])
  useEffect(() => {
    refresh()
  }, [refresh])
  return <Ctx.Provider value={{ session, refresh, logout }}>{children}</Ctx.Provider>
}

export const useSession = () => useContext(Ctx)
