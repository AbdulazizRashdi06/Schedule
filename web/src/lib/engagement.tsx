import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import { api, errorText } from './api'
import { startTracking, type Runner } from './engagementTracker'
import { useSession } from './session'

interface Consent { enabled: boolean; available: boolean; version: number }
interface Preference {
  consent: Consent | null
  error: string
  saving: boolean
  refresh: () => void
  change: (enabled: boolean) => Promise<void>
}
const Context = createContext<Preference>(null!)
export const useEngagement = () => useContext(Context)

export function EngagementProvider({ children }: { children: ReactNode }) {
  const { session } = useSession()
  const account = session?.kind === 'student' ? session.name ?? '' : ''
  const [saved, setSaved] = useState<{ account: string; value: Consent } | null>(null)
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const [revision, setRevision] = useState(0)
  const consent = saved?.account === account && account ? saved.value : null
  const { pathname } = useLocation()
  const path = useRef(pathname)
  const runner = useRef<Runner | null>(null)
  useEffect(() => {
    path.current = pathname
    runner.current?.navigate(pathname)
  }, [pathname])
  useEffect(() => {
    if (!account) return
    let live = true
    api.get<Consent>('/me/analytics/consent').then(value => {
      if (live) { setSaved({ account, value }); setError('') }
    }).catch(e => { if (live) setError(errorText(e)) })
    return () => { live = false }
  }, [account, revision])
  const enabled = consent?.enabled === true && consent.available
  useEffect(() => {
    if (!enabled) return
    const tracker = startTracking(path.current, () => setSaved(null))
    runner.current = tracker
    return () => { tracker.stop(); runner.current = null }
  }, [enabled, account])
  return <Context.Provider value={{ consent, error, saving, refresh: () => setRevision(x => x + 1), change: async enabled => {
    setSaving(true)
    setError('')
    if (!enabled) runner.current?.stop()
    try {
      await api.put('/me/analytics/consent', { enabled, version: consent?.version })
      setSaved({ account, value: { enabled, available: consent?.available ?? false, version: 1 } })
    } catch (e) {
      setError(errorText(e))
      // A failed revocation must not look successful; refresh the server state before restarting.
      setSaved(null)
    } finally { setSaving(false) }
  } }}>{children}</Context.Provider>
}
