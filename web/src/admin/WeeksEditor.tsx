import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import type { Weeks } from '../lib/types'
import { Button, ErrorText, TextField } from '../components/ui'

/** Week 1's start and the last week's number for a semester; every week in between counts on from week 1. */
export default function WeeksEditor({ semesterId, name }: { semesterId: number; name: string }) {
  const [weeks, setWeeks] = useState<Weeks | null>(null)
  const [start, setStart] = useState('')
  const [last, setLast] = useState('')
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  const show = (w: Weeks) => {
    setWeeks(w)
    setStart(w.week1Start ?? '')
    setLast(w.lastWeek?.toString() ?? '')
  }
  useEffect(() => {
    setStatus('')
    setError('')
    api.get<Weeks>(`/admin/semesters/${semesterId}/weeks`).then(show, (e) => setError(errorText(e)))
  }, [semesterId])

  const save = async (body: { week1Start: string | null; lastWeek: number | null }) => {
    setBusy(true)
    setError('')
    setStatus('')
    try {
      show(await api.put<Weeks>(`/admin/semesters/${semesterId}/weeks`, body))
      setStatus('Saved.')
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]">
      <div className="flex flex-col gap-1">
        <h3 className="text-[18px] font-extrabold">Teaching weeks · {name}</h3>
        <span className="text-[14px] font-semibold text-muted">
          Students see “Week 1”, “Week 2”… from this date. {weeks && !weeks.set && 'Worked out from the class dates until you set it.'}
        </span>
      </div>
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          const n = last.trim() ? Number(last) : null
          if (n !== null && !Number.isInteger(n)) return setError('The last week is a whole number, like 14.')
          save({ week1Start: start || null, lastWeek: n })
        }}
      >
        <TextField label="Week 1 starts" type="date" value={start} onChange={(e) => setStart(e.target.value)} className="w-[180px]" />
        <TextField label="Last week" inputMode="numeric" placeholder="14" value={last} onChange={(e) => setLast(e.target.value)} className="w-[110px]" mono />
        <Button type="submit" busy={busy} className="h-12 px-5 text-[15px]">
          Save
        </Button>
        {weeks?.set && (
          <Button type="button" variant="ghost" className="h-12 px-4 text-[14px]" onClick={() => save({ week1Start: null, lastWeek: null })}>
            Use class dates
          </Button>
        )}
      </form>
      {status && <span className="text-[14px] font-bold">{status}</span>}
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
