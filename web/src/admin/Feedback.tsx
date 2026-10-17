import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Tag } from '../components/ui'
import { PageTitle, shortDate } from './AdminLayout'

interface Prompt {
  id: number
  message: string
  by: string
  createdAt: string
  closedAt: string | null
  open: boolean
  ratings: number
  notNow: number
  average: number | null
  stars: number[]
  comments: { rating: number; comment: string; at: string }[]
}

const DEFAULT = 'Classy is in beta. How are you finding it? Tell us what to fix or add.'

/** Push a "Rate Classy" pop-up to every student and read the answers. Answers are anonymous. */
export default function Feedback() {
  const [prompts, setPrompts] = useState<Prompt[] | null>(null)
  const [message, setMessage] = useState(DEFAULT)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const load = () => api.get<Prompt[]>('/admin/feedback').then(setPrompts, (e) => setError(errorText(e)))
  useEffect(() => {
    void load()
  }, [])

  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError('')
    try {
      await fn()
      await load()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }
  const live = prompts?.find((p) => p.open)

  return (
    <>
      <PageTitle title="Feedback." sub="Ask students to rate Classy. Each student sees the pop-up once. Answers are anonymous." />
      <section className="flex max-w-[640px] flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
        <label htmlFor="rate-message" className="text-[20px] font-extrabold">
          Push a rate-us message
        </label>
        <textarea
          id="rate-message"
          value={message}
          maxLength={300}
          rows={3}
          onChange={(e) => setMessage(e.target.value)}
          className="rounded-[14px] bg-paper p-3 text-[16px] font-semibold text-navy outline-none"
        />
        {live && <p className="text-[14px] font-semibold text-muted">Pushing this ends the message that’s live now.</p>}
        <ErrorText>{error}</ErrorText>
        <Button className="self-start" busy={busy} disabled={!message.trim()} onClick={() => act(() => api.post('/admin/feedback', { message }))}>
          Push to students
        </Button>
      </section>

      {prompts?.length === 0 && <p className="text-[15px] font-semibold text-muted">Nothing pushed yet.</p>}
      {prompts?.map((p) => {
        const most = Math.max(1, ...p.stars)
        return (
          <section key={p.id} className="flex max-w-[720px] flex-col gap-3 border-t-[1.5px] border-navy/10 pt-5">
            <div className="flex flex-wrap items-center gap-2">
              {p.open ? <Tag tone="navy">Live</Tag> : <Tag tone="plain">Ended</Tag>}
              <span className="text-[14px] font-semibold text-muted">
                {shortDate(p.createdAt)} · {p.by}
              </span>
              {p.open && (
                <Button variant="secondary" size="sm" className="ml-auto" disabled={busy} onClick={() => act(() => api.post(`/admin/feedback/${p.id}/close`))}>
                  End it
                </Button>
              )}
            </div>
            <p className="text-[17px] font-bold whitespace-pre-wrap">{p.message}</p>
            <div className="flex flex-wrap items-end gap-6">
              <div className="flex flex-col">
                <span className="display text-[48px] leading-none tracking-[-0.04em]">{p.average?.toFixed(1) ?? '–'}</span>
                <span className="text-[14px] font-semibold text-muted">
                  {p.ratings} rating{p.ratings === 1 ? '' : 's'} · {p.notNow} not now
                </span>
              </div>
              <div className="flex min-w-[220px] flex-1 flex-col gap-1">
                {[5, 4, 3, 2, 1].map((s) => (
                  <div key={s} className="flex items-center gap-2 text-[13px] font-bold">
                    <span className="w-6 tabular-nums">{s}★</span>
                    <span className="h-2.5 flex-1 overflow-hidden rounded-full bg-navy/10">
                      <span className="block h-full rounded-full bg-navy" style={{ width: `${(p.stars[s - 1] / most) * 100}%` }} />
                    </span>
                    <span className="w-8 text-right tabular-nums">{p.stars[s - 1]}</span>
                  </div>
                ))}
              </div>
            </div>
            {p.comments.length > 0 && (
              <ul className="flex flex-col gap-2">
                {p.comments.map((c, i) => (
                  <li key={i} className="rounded-[14px] bg-paper p-3">
                    <span className="label text-[11px] tracking-[0.12em]">
                      {c.rating}★ · {shortDate(c.at)}
                    </span>
                    <p className="text-[15px] font-semibold break-words whitespace-pre-wrap">{c.comment}</p>
                  </li>
                ))}
              </ul>
            )}
          </section>
        )
      })}
    </>
  )
}
