import { useState } from 'react'
import { api, errorText } from '../lib/api'
import type { CustomSlot } from '../lib/types'
import { DAY_LONG } from '../lib/time'
import { Button, ErrorText, Select, Sheet, TextField, cx } from '../components/ui'

/** What a new slot starts with: the day (and date) and times the student tapped. */
export interface SlotPreset {
  day: number
  date: string | null
  start: string
  end: string
}

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/

/** Add or change one of the student's own slots: every week on a day, or once on a date, with an optional private note. */
export default function SlotSheet({ slot, preset, onClose, onSaved }: { slot: CustomSlot | null; preset: SlotPreset | null; onClose: () => void; onSaved: () => void }) {
  const open = !!slot || !!preset
  return (
    <Sheet open={open} onClose={onClose} label={slot ? 'Edit slot' : 'Add a slot'}>
      {open && <SlotForm key={slot?.id ?? `${preset?.day}-${preset?.date}-${preset?.start}`} slot={slot} preset={preset} onClose={onClose} onSaved={onSaved} />}
    </Sheet>
  )
}

function SlotForm({ slot, preset, onClose, onSaved }: { slot: CustomSlot | null; preset: SlotPreset | null; onClose: () => void; onSaved: () => void }) {
  const [title, setTitle] = useState(slot?.title ?? '')
  const [dated, setDated] = useState(slot ? !!slot.date : false)
  const [day, setDay] = useState(slot?.day ?? preset?.day ?? 0)
  const [date, setDate] = useState(slot?.date ?? preset?.date ?? '')
  const [start, setStart] = useState(slot?.start ?? preset?.start ?? '08:00')
  const [end, setEnd] = useState(slot?.end ?? preset?.end ?? '09:30')
  const [place, setPlace] = useState(slot?.place ?? '')
  const [note, setNote] = useState(slot?.note ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError('')
    try {
      await fn()
      onSaved()
      onClose()
    } catch (e) {
      setError(errorText(e))
      setBusy(false)
    }
  }

  return (
    <form
      className="flex flex-col gap-3.5 pt-1"
      onSubmit={(e) => {
        e.preventDefault()
        if (!title.trim()) return setError('Give the slot a name.')
        if (!TIME.test(start) || !TIME.test(end)) return setError('Enter times like 08:00.')
        if (end <= start) return setError('The slot ends before it starts.')
        if (dated && !date) return setError('Pick a date.')
        const body = { title, day, date: dated ? date : null, start, end, place, note }
        void run(() => (slot ? api.put(`/me/slots/${slot.id}`, body) : api.post('/me/slots', body)))
      }}
    >
      <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">{slot ? 'Your slot' : 'Add a slot'}</h2>
      <TextField label="Name" placeholder="Study, gym, meeting…" value={title} maxLength={60} onChange={(e) => setTitle(e.target.value)} autoFocus={!slot} />

      <div role="radiogroup" aria-label="Repeats" className="grid grid-cols-2 gap-1 rounded-full bg-navy/[0.07] p-1">
        {[
          { on: false, label: 'Every week' },
          { on: true, label: 'On a date' },
        ].map((o) => (
          <button
            key={o.label}
            type="button"
            role="radio"
            aria-checked={dated === o.on}
            onClick={() => setDated(o.on)}
            className={cx('h-11 rounded-full text-[15px] font-extrabold', dated === o.on ? 'bg-navy text-cream' : 'text-navy')}
          >
            {o.label}
          </button>
        ))}
      </div>

      {dated ? (
        <TextField label="Date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
      ) : (
        <label className="flex flex-col gap-1.5">
          <span className="label text-[11px] tracking-[0.12em]">Day</span>
          <Select value={day} onChange={(e) => setDay(Number(e.target.value))}>
            {DAY_LONG.map((name, k) => (
              <option key={k} value={k}>
                Every {name}
              </option>
            ))}
          </Select>
        </label>
      )}
      <div className="grid grid-cols-2 gap-2">
        <TextField label="Starts" mono inputMode="numeric" placeholder="08:00" value={start} invalid={!TIME.test(start)} onChange={(e) => setStart(e.target.value)} />
        <TextField label="Ends" mono inputMode="numeric" placeholder="09:30" value={end} invalid={!TIME.test(end) || end <= start} onChange={(e) => setEnd(e.target.value)} />
      </div>
      <TextField label="Place (optional)" placeholder="Library, gym…" value={place} maxLength={60} onChange={(e) => setPlace(e.target.value)} />
      <label className="flex flex-col gap-1.5">
        <span className="label text-[11px] tracking-[0.12em]">Note (only you see it)</span>
        <textarea
          value={note}
          maxLength={500}
          rows={3}
          onChange={(e) => setNote(e.target.value)}
          className="field rounded-[14px] bg-paper p-3 text-[16px] font-semibold text-navy outline-none"
        />
      </label>

      <ErrorText>{error}</ErrorText>
      <div className="flex gap-2.5 pt-1">
        {slot && (
          <Button type="button" variant="danger" className="h-[52px] px-5" disabled={busy} onClick={() => run(() => api.del(`/me/slots/${slot.id}`))}>
            Delete
          </Button>
        )}
        <Button type="submit" className="h-[52px] flex-1" busy={busy}>
          {busy ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </form>
  )
}
