import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { ApiError, api, store } from './api'
import type { SessionInfo } from './types'

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
      const saved = e instanceof ApiError && e.status === 0 ? store.get(SAVED) : null
      const s: SessionInfo = saved ? JSON.parse(saved) : { kind: null }
      setSession(s)
      return s
    }
  }, [])
  const logout = useCallback(async () => {
    await api.post('/auth/logout').catch(() => {})
    store.remove(SAVED)
    store.remove('timetable')
    setSession({ kind: null })
  }, [])
  useEffect(() => {
    refresh()
  }, [refresh])
  return <Ctx.Provider value={{ session, refresh, logout }}>{children}</Ctx.Provider>
}

export const useSession = () => useContext(Ctx)
