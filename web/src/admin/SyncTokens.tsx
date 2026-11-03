import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Tag } from '../components/ui'
import { shortDate } from './AdminLayout'

interface Token {
  id: number
  name: string
  createdBy: string
  createdAt: string
  lastUsedAt: string | null
  canSendEnrollments: boolean
}

/**
 * Browsers connected with the Classy Sync extension, and the token GUtech IT uses to send students' enrollments.
 * Disconnecting one stops it sending. Only a GUtech IT token can send enrollments; the extension's only send timetables.
 */
export default function SyncTokens() {
  const [tokens, setTokens] = useState<Token[] | null>(null)
  const [error, setError] = useState('')
  const [made, setMade] = useState<string | null>(null)
  const load = () => api.get<Token[]>('/admin/sync-tokens').then(setTokens, (e) => setError(errorText(e)))
  useEffect(() => {
    void load()
  }, [])

  return (
    <section className="flex flex-col gap-2">
      <h2 className="text-[24px] font-extrabold">Classy Sync</h2>
      <p className="text-[15px] font-semibold text-muted">Browsers with the extension that fetches the timetable from EduWave, and GUtech IT's enrollments token.</p>
      <ErrorText>{error}</ErrorText>
      <div className="flex flex-col border-t-[2.5px] border-navy">
        {tokens?.length === 0 && <p className="px-2 py-4 text-[15px] font-semibold text-muted">None connected.</p>}
        {tokens?.map((t) => (
          <div key={t.id} className="grid grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_auto] items-center gap-4 border-b-[1.5px] border-navy/10 px-2 py-4">
            <span className="flex items-center gap-2 text-[16px] font-bold">
              {t.name}
              {t.canSendEnrollments && <Tag>Enrollments</Tag>}
            </span>
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
      {made ? (
        <div className="flex flex-col gap-1.5 rounded-[14px] bg-ice px-3 py-3">
          <span className="label text-[11px]">GUtech IT token · shown once, copy it now</span>
          <code className="font-mono text-[14px] font-bold break-all select-all">{made}</code>
        </div>
      ) : (
        <Button
          variant="secondary"
          size="sm"
          className="self-start"
          onClick={async () => {
            try {
              const r = await api.post<{ token: string }>('/admin/sync-tokens', { name: 'GUtech IT', enrollments: true })
              setMade(r.token)
              await load()
            } catch (e) {
              setError(errorText(e))
            }
          }}
        >
          Make a token for GUtech IT
        </Button>
      )}
    </section>
  )
}
