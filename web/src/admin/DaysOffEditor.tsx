import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, TextField } from '../components/ui'

interface DayOff {
  id: number
  date: string
  name: string
}

/** "Tue 18 Nov" for a "2026-11-18" date. */
const fmt = (iso: string) => new Date(`${iso}T12:00`).toLocaleDateString('en-GB', { weekday: 'short', day: 'numeric', month: 'short' })

/**
 * Days with no classes in a semester: public holidays and breaks. Weekly classes skip them for every student, in the app,
 * their calendar feeds and reminders. A class put on that exact date still shows.
 */
export default function DaysOffEditor({ semesterId, name }: { semesterId: number; name: string }) {
  const [days, setDays] = useState<DayOff[] | null>(null)
  const [date, setDate] = useState('')
  const [to, setTo] = useState('')
  const [label, setLabel] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const url = `/admin/semesters/${semesterId}/days-off`

  useEffect(() => {
    setError('')
    api.get<DayOff[]>(url).then(setDays, (e) => setError(errorText(e)))
  }, [url])

  const add = async () => {
    setBusy(true)
    setError('')
    try {
      setDays(await api.post<DayOff[]>(url, { date, to: to || null, name: label }))
      setDate('')
      setTo('')
      setLabel('')
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (d: DayOff) => {
    setError('')
    try {
      await api.del(`${url}/${d.id}`)
      setDays((all) => all?.filter((x) => x.id !== d.id) ?? null)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]">
      <div className="flex flex-col gap-1">
        <h3 className="text-[18px] font-extrabold">Days off · {name}</h3>
        <span className="text-[14px] font-semibold text-muted">Holidays and breaks. Weekly classes skip these days for everyone, calendars and reminders included.</span>
      </div>
      {days && days.length > 0 && (
        <ul className="flex flex-col">
          {days.map((d) => (
            <li key={d.id} className="flex items-center justify-between gap-3 border-b-[1.5px] border-navy/10 py-2">
              <span className="flex min-w-0 items-baseline gap-2.5">
                <span className="label shrink-0 text-[12px]">{fmt(d.date)}</span>
                <span className="truncate text-[15px] font-bold">{d.name}</span>
              </span>
              <Button variant="ghost" size="sm" onClick={() => remove(d)} aria-label={`Remove ${d.name} on ${fmt(d.date)}`}>
                Remove
              </Button>
            </li>
          ))}
        </ul>
      )}
      {days?.length === 0 && <p className="text-[14px] font-semibold text-muted">None yet.</p>}
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          void add()
        }}
      >
        <TextField label="Day off" type="date" value={date} onChange={(e) => setDate(e.target.value)} className="w-[170px]" />
        <TextField label="Until (optional)" type="date" value={to} min={date || undefined} onChange={(e) => setTo(e.target.value)} className="w-[170px]" />
        <TextField label="Name" placeholder="National Day" value={label} onChange={(e) => setLabel(e.target.value)} className="w-[200px]" />
        <Button type="submit" busy={busy} disabled={!date || !label.trim()} className="h-12 px-5 text-[15px]">
          Add
        </Button>
      </form>
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
