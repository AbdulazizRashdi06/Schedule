import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import type { SessionInfo } from './types'

interface SessionCtx {
  session: SessionInfo | null
  refresh: () => Promise<SessionInfo>
  logout: () => Promise<void>
}

const Ctx = createContext<SessionCtx>(null!)

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<SessionInfo | null>(null)
  const refresh = useCallback(async () => {
    try {
      const s = await api.get<SessionInfo>('/auth/session')
      setSession(s)
      return s
    } catch {
      const s: SessionInfo = { kind: null }
      setSession(s)
      return s
    }
  }, [])
  const logout = useCallback(async () => {
    await api.post('/auth/logout').catch(() => {})
    setSession({ kind: null })
  }, [])
  useEffect(() => {
    refresh()
  }, [refresh])
  return <Ctx.Provider value={{ session, refresh, logout }}>{children}</Ctx.Provider>
}

export const useSession = () => useContext(Ctx)
