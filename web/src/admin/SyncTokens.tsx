import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText } from '../components/ui'
import { shortDate } from './AdminLayout'

interface Token {
  id: number
  name: string
  createdBy: string
  createdAt: string
  lastUsedAt: string | null
}

/** Browsers connected with the Classy Sync extension. Disconnecting one stops it sending timetables. */
export default function SyncTokens() {
  const [tokens, setTokens] = useState<Token[] | null>(null)
  const [error, setError] = useState('')
  const load = () => api.get<Token[]>('/admin/sync-tokens').then(setTokens, (e) => setError(errorText(e)))
  useEffect(() => {
    void load()
  }, [])

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-[24px] font-extrabold">Classy Sync</h2>
      <p className="text-[15px] font-semibold text-muted">Browsers with the extension that fetches the timetable from EduWave.</p>
      <ErrorText>{error}</ErrorText>
      <div className="flex flex-col border-t-[2.5px] border-navy">
        {tokens?.length === 0 && <p className="px-2 py-4 text-[15px] font-semibold text-muted">None connected.</p>}
        {tokens?.map((t) => (
          <div key={t.id} className="grid grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_auto] items-center gap-4 border-b-[1.5px] border-navy/10 px-2 py-4">
            <span className="text-[16px] font-bold">{t.name}</span>
            <span className="text-[14px] font-semibold text-muted">
              {t.createdBy} · {t.lastUsedAt ? `last sync ${shortDate(t.lastUsedAt)}` : 'not used yet'}
            </span>
            <Button
              variant="secondary"
              size="sm"
              onClick={async () => {
                try {
                  await api.del(`/admin/sync-tokens/${t.id}`)
                  await load()
                } catch (e) {
                  setError(errorText(e))
                }
              }}
            >
              Disconnect
            </Button>
          </div>
        ))}
      </div>
    </section>
  )
}
