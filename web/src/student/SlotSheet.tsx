import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { api, errorText } from '../lib/api'
import type { CustomSlot } from '../lib/types'
import { DAY_LONG } from '../lib/time'
import { SLOTS } from '../lib/slots'
import { Button, ErrorText, Icon, Select, Sheet, TextField, cx } from '../components/ui'

/** What a new slot starts with: the day (and date) and times the student tapped. */
export interface SlotPreset {
  day: number
  date: string | null
  start: string
  end: string
}

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/
const pad = (n: number) => String(n).padStart(2, '0')
const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
const fromMin = (m: number) => `${pad(Math.floor(m / 60) % 24)}:${pad(m % 60)}`
/** A class is 1h30: a new start pushes the end along by that much. */
const CLASS_MIN = 90

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
  const [clock, setClock] = useState<'start' | 'end' | null>(null)

  /** Moving the start keeps the slot's length, or a class's length if the end would fall before it. */
  const changeStart = (v: string) => {
    const len = TIME.test(start) && TIME.test(end) && end > start ? toMin(end) - toMin(start) : CLASS_MIN
    setStart(v)
    setEnd(fromMin(Math.min(toMin(v) + len, 23 * 60 + 59)))
  }

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
        <TimeButton label="Starts" value={start} open={clock === 'start'} onOpen={() => setClock(clock === 'start' ? null : 'start')} />
        <TimeButton label="Ends" value={end} open={clock === 'end'} invalid={end <= start} onOpen={() => setClock(clock === 'end' ? null : 'end')} />
      </div>
      {clock && (
        <Clock
          key={clock}
          label={clock === 'start' ? 'Start time' : 'End time'}
          value={clock === 'start' ? start : end}
          onChange={clock === 'start' ? changeStart : setEnd}
          chips={SLOTS.map(([s, e]) => (clock === 'start' ? s : e))}
          onDone={() => setClock(null)}
        />
      )}
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
      <div className="flex gap-2.5 pt-1" onClick={() => setClock(null)}>
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

/** A time field that opens the clock instead of a keyboard: the field's look, the time in mono, a clock icon. */
function TimeButton({ label, value, open, invalid, onOpen }: { label: string; value: string; open: boolean; invalid?: boolean; onOpen: () => void }) {
  return (
    <div className="flex flex-col gap-1.5">
      <span className="label text-[11px] tracking-[0.12em]">{label}</span>
      <button
        type="button"
        aria-expanded={open}
        aria-label={`${label} ${value}, change`}
        onClick={onOpen}
        data-invalid={invalid || undefined}
        className={cx(
          'field press flex h-12 items-center justify-between rounded-[14px] bg-paper px-3.5 font-mono text-[16px] font-bold text-navy',
          open && 'shadow-[inset_0_0_0_2.5px_var(--color-navy),0_0_0_4px_var(--color-ice)]!',
        )}
      >
        {value}
        <Icon name="clock" className="size-[18px]" />
      </button>
    </div>
  )
}

const FACE = 224
const MID = FACE / 2
const OUTER = 88
const INNER = 58

/** Where a number sits on the face: `k` of 12 positions round from the top, at `r` from the middle. */
const spot = (k: number, r: number) => ({ left: MID + r * Math.sin((k * Math.PI) / 6), top: MID - r * Math.cos((k * Math.PI) / 6) })

/**
 * A small 24-hour clock. Tap or drag on the face to set the hour (1–12 round the outside, 13–23 and 00 inside), and
 * it moves on to the minutes, in fives. The hour and minute in the readout switch between the two, and take the arrow
 * keys. Chips under the face set a class slot's time in one tap.
 */
function Clock({ label, value, onChange, chips, onDone }: { label: string; value: string; onChange: (v: string) => void; chips: readonly string[]; onDone: () => void }) {
  const [part, setPart] = useState<'h' | 'm'>('h')
  const face = useRef<HTMLDivElement>(null)
  const dragging = useRef(false)
  const h = Number(value.slice(0, 2)) || 0
  const m = Number(value.slice(3, 5)) || 0
  const set = (hh: number, mm: number) => onChange(`${pad(((hh % 24) + 24) % 24)}:${pad(((mm % 60) + 60) % 60)}`)

  const pick = (e: PointerEvent, done: boolean) => {
    const r = face.current?.getBoundingClientRect()
    if (!r) return
    const x = e.clientX - r.left - MID
    const y = e.clientY - r.top - MID
    const turn = (Math.atan2(x, -y) / (2 * Math.PI) + 1) % 1
    if (part === 'h') {
      const k = Math.round(turn * 12) % 12
      const inner = Math.hypot(x, y) < (OUTER + INNER) / 2
      set(inner ? (k === 0 ? 0 : k + 12) : k === 0 ? 12 : k, m)
      if (done) setPart('m')
    } else set(h, (Math.round(turn * 12) * 5) % 60)
  }

  const onKey = (which: 'h' | 'm') => (e: KeyboardEvent) => {
    const step = e.key === 'ArrowUp' ? 1 : e.key === 'ArrowDown' ? -1 : 0
    if (!step) return
    e.preventDefault()
    if (which === 'h') set(h + step, m)
    else set(h, m + step * 5)
  }

  // The hand: to the hour (outer or inner ring) or to the minute.
  const k = part === 'h' ? h % 12 : m / 5
  const r = part === 'h' && (h === 0 || h > 12) ? INNER : OUTER
  const knob = spot(k, r)
  const numbers =
    part === 'h'
      ? [
          ...Array.from({ length: 12 }, (_, i) => ({ v: i === 0 ? 12 : i, k: i, r: OUTER, inner: false })),
          ...Array.from({ length: 12 }, (_, i) => ({ v: i === 0 ? 0 : i + 12, k: i, r: INNER, inner: true })),
        ]
      : Array.from({ length: 12 }, (_, i) => ({ v: i * 5, k: i, r: OUTER, inner: false }))
  const chosen = part === 'h' ? h : m

  return (
    <div role="group" aria-label={label} className="open flex flex-col items-center gap-3 rounded-[24px] bg-paper p-4 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]">
      <div className="flex w-full items-center justify-between">
        <div className="flex items-center font-mono text-[30px] leading-none font-bold">
          {(['h', 'm'] as const).map((p, i) => (
            <span key={p} className="flex items-center">
              {i === 1 && <span className="px-0.5">:</span>}
              <button
                type="button"
                aria-pressed={part === p}
                aria-label={p === 'h' ? `Hour ${pad(h)}` : `Minutes ${pad(m)}`}
                onClick={() => setPart(p)}
                onKeyDown={onKey(p)}
                className={cx('press rounded-[10px] px-1.5 py-1 tabular-nums', part === p ? 'bg-navy text-cream' : 'tint')}
              >
                {pad(p === 'h' ? h : m)}
              </button>
            </span>
          ))}
        </div>
        <Button type="button" size="sm" onClick={onDone}>
          Done
        </Button>
      </div>

      <div
        ref={face}
        aria-hidden="true"
        className="relative touch-none rounded-full bg-cream select-none"
        style={{ width: FACE, height: FACE }}
        onPointerDown={(e) => {
          dragging.current = true
          e.currentTarget.setPointerCapture(e.pointerId)
          pick(e, false)
        }}
        onPointerMove={(e) => dragging.current && pick(e, false)}
        onPointerUp={(e) => {
          if (!dragging.current) return
          dragging.current = false
          pick(e, true)
        }}
        // A drag the system takes over (or a lost capture) ends without picking, so stray moves can't change the time.
        onPointerCancel={() => (dragging.current = false)}
        onLostPointerCapture={() => (dragging.current = false)}
      >
        <svg width={FACE} height={FACE} className="absolute inset-0">
          <line x1={MID} y1={MID} x2={knob.left} y2={knob.top} className="stroke-navy" strokeWidth={2} />
          <circle cx={MID} cy={MID} r={3.5} className="fill-navy" />
          <circle cx={knob.left} cy={knob.top} r={16} className="fill-navy" />
        </svg>
        {numbers.map((n) => {
          const at = spot(n.k, n.r)
          return (
            <span
              key={`${n.inner}-${n.v}`}
              className={cx(
                'pointer-events-none absolute grid size-8 -translate-x-1/2 -translate-y-1/2 place-items-center font-mono font-bold tabular-nums',
                n.inner ? 'text-[11px]' : 'text-[13px]',
                n.v === chosen && (part === 'm' || n.inner === (h === 0 || h > 12)) ? 'text-cream' : n.inner ? 'text-muted' : 'text-navy',
              )}
              style={{ left: at.left, top: at.top }}
            >
              {pad(n.v)}
            </span>
          )
        })}
      </div>

      <div className="flex w-full flex-wrap justify-center gap-1.5">
        {chips.map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => onChange(t)}
            className={cx('press label h-9 rounded-full px-3 text-[11px] tracking-[0.06em]', t === value ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.2)]')}
          >
            {t}
          </button>
        ))}
      </div>
    </div>
  )
}
