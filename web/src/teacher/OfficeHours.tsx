import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { DAY_LONG } from '../lib/time'
import { SLOTS } from '../lib/slots'
import { useHome } from '../lib/viewer'
import { Button, Checkbox, ErrorText, Select, Sk, TextField, Toast, cx, useToast } from '../components/ui'
import { Clock, TimeButton } from '../student/SlotSheet'
import { BackButton } from '../student/timetable'
import { clearTeachCache } from './teachData'

/** What students see of a teacher besides their classes (GET and PUT /teach/office). */
interface Office {
  name: string
  email: string | null
  officeRoom: string
  note: string
  showEmail: boolean
  hours: { id?: number; day: number; start: string; end: string; room: string }[]
}

/** A row being edited. `key` only tells the rows apart on this page. */
interface Row {
  key: number
  day: number
  start: string
  end: string
  room: string
}

/** The server's limits (TeachEndpoints). */
const MAX_HOURS = 10
const NOTE_MAX = 200

const pad = (n: number) => String(n).padStart(2, '0')
const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5))
const fromMin = (m: number) => `${pad(Math.floor(m / 60) % 24)}:${pad(m % 60)}`

/**
 * Office hours & details: the weekly times students can find the teacher, with a room each, and the office room, a
 * short note and (only if the teacher turns it on) their email. Office hours show on the teacher's own timetable and
 * to students on every class they teach.
 */
export default function OfficeHours() {
  const navigate = useNavigate()
  const { teach } = useHome()
  const [office, setOffice] = useState<Office | null>(null)
  const [rows, setRows] = useState<Row[]>([])
  const [officeRoom, setOfficeRoom] = useState('')
  const [note, setNote] = useState('')
  const [showEmail, setShowEmail] = useState(false)
  /** Which row's clock is open, and for which end of it. */
  const [clock, setClock] = useState<{ key: number; part: 'start' | 'end' } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const toast = useToast()
  const nextKey = useRef(0)

  const fill = (o: Office) => {
    setOffice(o)
    setRows(o.hours.map((h) => ({ key: nextKey.current++, day: h.day, start: h.start, end: h.end, room: h.room })))
    setOfficeRoom(o.officeRoom)
    setNote(o.note)
    setShowEmail(o.showEmail)
  }

  useEffect(() => {
    api.get<Office>('/teach/office').then(fill, (e) => setError(errorText(e)))
  }, [])

  const change = (key: number, next: Partial<Row>) => setRows((list) => list.map((r) => (r.key === key ? { ...r, ...next } : r)))
  /** Moving the start keeps the hour's length. */
  const changeStart = (r: Row, v: string) => change(r.key, { start: v, end: fromMin(Math.min(toMin(v) + Math.max(toMin(r.end) - toMin(r.start), 15), 23 * 60 + 59)) })
  const add = () => {
    // After the last one, on the next day: most office hours are one a day.
    const last = rows[rows.length - 1]
    const row: Row = { key: nextKey.current++, day: last ? (last.day + 1) % 5 : 0, start: last?.start ?? '10:00', end: last?.end ?? '11:00', room: last?.room ?? officeRoom }
    setRows((list) => [...list, row])
  }

  // Only the teacher can show their own email; an owner looking in may only turn it off.
  const emailLocked = teach != null && !teach.personal && !office?.showEmail

  const save = async () => {
    if (busy) return
    if (rows.some((r) => r.end <= r.start)) return setError('An office hour ends before it starts.')
    setBusy(true)
    setError('')
    setClock(null)
    try {
      fill(await api.put<Office>('/teach/office', { officeRoom, note, showEmail, hours: rows.map(({ day, start, end, room }) => ({ day, start, end, room })) }))
      // The home draws the office hours the next time it's opened.
      clearTeachCache()
      toast.show('Saved. Students see this on your classes.')
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const card = 'bg-paper shadow-[0_1px_0_rgb(11_25_87/0.06),0_8px_20px_-14px_rgb(11_25_87/0.35)]'

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <form
        className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(2.5rem,env(safe-area-inset-bottom))]"
        onSubmit={(e) => {
          e.preventDefault()
          void save()
        }}
      >
        <header className="flex items-center justify-between px-6">
          <BackButton label="Back to today" onClick={() => navigate('/teach')} />
          {teach?.semester && <span className="label text-[12px] tracking-[0.14em]">{teach.semester.name}</span>}
        </header>
        <div className="flex flex-col gap-2 px-6 pt-5">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.05em]">Office hours</h1>
          <p className="text-[16px] leading-snug font-semibold text-muted">Students see these on every class you teach. They skip holidays and the weeks outside teaching.</p>
        </div>

        {!office ? (
          <div className="px-6 pt-5">
            {error ? (
              <ErrorText>{error}</ErrorText>
            ) : (
              <div aria-busy="true" aria-label="Loading your office hours" className="flex flex-col gap-2.5">
                {[0, 1].map((i) => (
                  <Sk key={i} className="h-[120px] rounded-[20px]" />
                ))}
              </div>
            )}
          </div>
        ) : (
          <>
            <section aria-label="Weekly office hours" className="flex flex-col gap-2.5 px-6 pt-5">
              <h2 className="label text-[12px] tracking-[0.14em] text-muted">Every week</h2>
              {rows.map((r, i) => {
                const open = clock?.key === r.key ? clock.part : null
                const toggle = (part: 'start' | 'end') => setClock(open === part ? null : { key: r.key, part })
                return (
                  <div key={r.key} role="group" aria-label={`Office hour ${i + 1}`} className={cx('flex flex-col gap-2.5 rounded-[20px] p-3', card)}>
                    <div className="flex items-end gap-2">
                      <label className="flex min-w-0 flex-1 flex-col gap-1.5">
                        <span className="label text-[11px] tracking-[0.12em]">Day</span>
                        <Select value={r.day} onChange={(e) => change(r.key, { day: Number(e.target.value) })}>
                          {DAY_LONG.map((name, k) => (
                            <option key={k} value={k}>
                              Every {name}
                            </option>
                          ))}
                        </Select>
                      </label>
                      <button
                        type="button"
                        onClick={() => {
                          setRows((list) => list.filter((x) => x.key !== r.key))
                          if (clock?.key === r.key) setClock(null)
                        }}
                        aria-label={`Remove office hour ${i + 1}`}
                        className="press label h-12 shrink-0 px-2 text-[12px] tracking-[0.08em] text-alert underline underline-offset-[3px]"
                      >
                        Remove
                      </button>
                    </div>
                    <div className="grid grid-cols-2 gap-2">
                      <TimeButton label="Starts" value={r.start} open={open === 'start'} onOpen={() => toggle('start')} />
                      <TimeButton label="Ends" value={r.end} open={open === 'end'} invalid={r.end <= r.start} onOpen={() => toggle('end')} />
                    </div>
                    {open && (
                      <Clock
                        key={open}
                        label={open === 'start' ? 'Start time' : 'End time'}
                        value={open === 'start' ? r.start : r.end}
                        onChange={(v) => (open === 'start' ? changeStart(r, v) : change(r.key, { end: v }))}
                        chips={SLOTS.map(([s, e]) => (open === 'start' ? s : e))}
                        onDone={() => setClock(null)}
                      />
                    )}
                    <TextField label="Room" placeholder="B-204" value={r.room} maxLength={60} onChange={(e) => change(r.key, { room: e.target.value })} />
                  </div>
                )
              })}
              {rows.length === 0 && <p className="text-[16px] font-semibold text-muted">None yet.</p>}
              {rows.length < MAX_HOURS && (
                <button type="button" onClick={add} className="label flex min-h-11 items-center self-start text-[12px] link-hover">
                  + Add an office hour
                </button>
              )}
            </section>

            <section aria-label="Details" className="flex flex-col gap-3.5 px-6 pt-7">
              <h2 className="label text-[12px] tracking-[0.14em] text-muted">Details</h2>
              <TextField label="Office room (optional)" placeholder="B-204" value={officeRoom} maxLength={60} onChange={(e) => setOfficeRoom(e.target.value)} />
              <label className="flex flex-col gap-1.5">
                <span className="label flex justify-between text-[11px] tracking-[0.12em]">
                  Note (optional)
                  <span className="text-muted">
                    {note.length}/{NOTE_MAX}
                  </span>
                </span>
                <textarea
                  value={note}
                  maxLength={NOTE_MAX}
                  rows={3}
                  placeholder="Away this week, email first…"
                  onChange={(e) => setNote(e.target.value)}
                  className="field rounded-[14px] bg-paper p-3 text-[16px] font-semibold text-navy outline-none placeholder:text-[#9AA3C7]"
                />
              </label>
              <div className={cx('flex items-start gap-3', emailLocked && 'opacity-60')}>
                <Checkbox checked={showEmail} onChange={(v) => !emailLocked && setShowEmail(v)} label="Show my email to students" />
                <span className="flex min-w-0 flex-col gap-0.5">
                  <span className="text-[16px] leading-tight font-extrabold">Show my email to students</span>
                  <span className="text-[13px] font-semibold break-all text-muted">{emailLocked ? 'Only the teacher can turn this on.' : (office.email ?? 'Off unless you turn it on.')}</span>
                </span>
              </div>
            </section>

            <div className="mt-auto flex flex-col gap-3 px-6 pt-8" aria-live="polite">
              <ErrorText>{error}</ErrorText>
              <Button type="submit" className="h-[60px] text-[18px]" busy={busy}>
                {busy ? 'Saving…' : 'Save'}
              </Button>
            </div>
          </>
        )}
      </form>
      <Toast toast={toast} className="inset-x-6 bottom-6 mx-auto max-w-[432px] rounded-[20px] bg-ice px-[18px] py-4 text-[16px] font-extrabold shadow-[0_12px_24px_-14px_rgb(11_25_87/0.5)]" />
    </div>
  )
}
