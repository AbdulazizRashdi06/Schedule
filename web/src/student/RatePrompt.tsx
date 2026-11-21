import { useEffect, useState } from 'react'
import { ApiError, api, errorText } from '../lib/api'
import { Button, ErrorText, Modal, cx } from '../components/ui'

interface Prompt {
  id: number
  message: string
}

const STAR = 'M12 2.5l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4l-5.9 3.1 1.2-6.5-4.8-4.6 6.6-.9z'

/** The "Rate Classy" pop-up staff push from the dashboard. Shown once per message; "Not now" counts as an answer. */
export default function RatePrompt() {
  const [prompt, setPrompt] = useState<Prompt | null>(null)
  const [open, setOpen] = useState(false)
  const [rating, setRating] = useState(0)
  const [comment, setComment] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [thanks, setThanks] = useState(false)

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    api.get<{ prompt: Prompt | null }>('/me/feedback').then(
      (r) => {
        if (!r.prompt) return
        setPrompt(r.prompt)
        timer = setTimeout(() => setOpen(true), 1200)
      },
      () => {},
    )
    return () => clearTimeout(timer)
  }, [])

  if (!prompt) return null

  // Closing always closes, even offline or after staff closed the question. If "Not now" doesn't reach the server, the
  // pop-up just asks again next time.
  const dismiss = () => {
    setOpen(false)
    if (!thanks) api.post('/me/feedback', { promptId: prompt.id, rating: null, comment: null, dismissed: true }).catch(() => {})
  }

  const send = async () => {
    setBusy(true)
    setError('')
    try {
      await api.post('/me/feedback', { promptId: prompt.id, rating, comment, dismissed: false })
      setThanks(true)
    } catch (e) {
      // 404: staff closed or replaced this question while it was open, so there is nothing left to answer.
      if (e instanceof ApiError && e.status === 404) setOpen(false)
      else setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal open={open} onClose={dismiss} label="Rate Classy">
      {thanks ? (
        <>
          <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Thank you!</h2>
          <p className="text-[16px] font-semibold text-muted">That helps us make Classy better.</p>
          <div className="flex justify-end">
            <Button onClick={() => setOpen(false)}>Done</Button>
          </div>
        </>
      ) : (
        <>
          <div className="flex flex-col gap-2">
            <span className="label text-[11px] tracking-[0.14em]">Classy beta</span>
            <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Rate Classy</h2>
            <p className="text-[16px] leading-snug font-semibold whitespace-pre-wrap text-muted">{prompt.message}</p>
          </div>
          <div role="radiogroup" aria-label="Stars" className="flex justify-center gap-1.5">
            {[1, 2, 3, 4, 5].map((n) => (
              <button
                key={n}
                type="button"
                role="radio"
                aria-checked={rating === n}
                aria-label={`${n} star${n > 1 ? 's' : ''}`}
                onClick={() => setRating(n)}
                className="press grid size-12 place-items-center rounded-full"
              >
                <svg viewBox="0 0 24 24" className={cx('size-9', n <= rating ? 'text-navy' : 'text-navy/20')} fill="currentColor" aria-hidden="true">
                  <path d={STAR} strokeLinejoin="round" stroke="currentColor" strokeWidth={1.5} />
                </svg>
              </button>
            ))}
          </div>
          <label htmlFor="rate-comment" className="sr">
            Comment
          </label>
          <textarea
            id="rate-comment"
            value={comment}
            maxLength={1000}
            rows={3}
            placeholder="What should we fix or add? (optional)"
            onChange={(e) => setComment(e.target.value)}
            className="rounded-[14px] bg-paper p-3 text-[15px] font-semibold text-navy outline-none placeholder:text-navy/45"
          />
          <ErrorText>{error}</ErrorText>
          <div className="flex justify-end gap-2.5">
            <Button variant="secondary" disabled={busy} onClick={dismiss}>
              Not now
            </Button>
            <Button busy={busy} disabled={rating === 0} onClick={() => void send()}>
              Send
            </Button>
          </div>
        </>
      )}
    </Modal>
  )
}
