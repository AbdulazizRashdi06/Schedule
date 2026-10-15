import { useEffect, useMemo, useState } from 'react'
import { api, errorText } from '../lib/api'
import type { Section } from '../lib/types'
import { addDays, isoDate } from '../lib/slots'
import { DAY_SHORT } from '../lib/time'
import { ErrorText, cx } from '../components/ui'

interface Note {
  id: number
  author: string
  mine: boolean
  isPublic: boolean
  date: string | null
  text: string
  createdAt: string
  edited: boolean
  hidden: boolean
  reported: boolean
}

const MAX = 500
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const label = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  const date = new Date(y, m - 1, d)
  return `${DAY_SHORT[date.getDay()]} ${d} ${MONTHS[m - 1]}`
}

/** Upcoming class dates of the section: its dated classes, or the next ten weeks of its weekly ones. */
function upcomingDates(section: Section, today: Date) {
  const from = isoDate(today)
  const dated = section.meetings.filter((m) => m.date && m.date >= from).map((m) => m.date!)
  if (dated.length) return [...new Set(dated)].sort().slice(0, 30)
  const days = new Set(section.meetings.filter((m) => !m.date).map((m) => m.day))
  return Array.from({ length: 70 }, (_, i) => addDays(today, i)).filter((d) => days.has(d.getDay())).map(isoDate).slice(0, 20)
}

/** Notes on a class: private ones only you see, public ones your classmates see (with your student ID). */
export default function ClassNotes({ courseId, section, me, onChanged }: { courseId: number; section: Section; me: string; onChanged: () => void }) {
  const [notes, setNotes] = useState<Note[] | null>(null)
  const [text, setText] = useState('')
  const [isPublic, setIsPublic] = useState(false)
  const [date, setDate] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [editing, setEditing] = useState<{ id: number; text: string } | null>(null)
  const dates = useMemo(() => upcomingDates(section, new Date()), [section])

  useEffect(() => {
    api.get<Note[]>(`/me/notes?courseId=${courseId}`).then(setNotes, (e) => setError(errorText(e)))
  }, [courseId])

  const act = async (fn: () => Promise<void>) => {
    setError('')
    try {
      await fn()
      onChanged()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <h2 className="text-[18px] font-extrabold">Notes</h2>
      <form
        className="flex flex-col gap-2 rounded-[18px] bg-navy-800 p-3"
        onSubmit={(e) => {
          e.preventDefault()
          if (!text.trim()) return
          setBusy(true)
          void act(async () => {
            const n = await api.post<Note>('/me/notes', { courseId, text, isPublic, date: date || null })
            setNotes([n, ...(notes ?? [])])
            setText('')
          }).finally(() => setBusy(false))
        }}
      >
        <label className="sr" htmlFor="note-text">
          New note
        </label>
        <textarea
          id="note-text"
          value={text}
          maxLength={MAX}
          rows={2}
          placeholder={isPublic ? 'Share with your classmates…' : 'A note only you see…'}
          onChange={(e) => setText(e.target.value)}
          className="rounded-[12px] bg-cream p-3 text-[15px] font-semibold text-navy outline-none placeholder:text-navy/45"
        />
        <div className="flex flex-wrap items-center gap-2">
          <div role="radiogroup" aria-label="Who sees it" className="flex rounded-full bg-navy p-0.5">
            {[
              { on: false, label: 'Private' },
              { on: true, label: 'Public' },
            ].map((o) => (
              <button
                key={o.label}
                type="button"
                role="radio"
                aria-checked={isPublic === o.on}
                onClick={() => setIsPublic(o.on)}
                className={cx('label h-9 rounded-full px-3.5 text-[10px]', isPublic === o.on ? 'bg-sky text-navy' : 'text-ice')}
              >
                {o.label}
              </button>
            ))}
          </div>
          <select
            aria-label="For"
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="label h-9 min-w-0 flex-1 rounded-full bg-navy px-3 text-[10px] text-ice outline-none"
          >
            <option value="">Whole class</option>
            {dates.map((d) => (
              <option key={d} value={d}>
                {label(d)}
              </option>
            ))}
          </select>
          <button type="submit" disabled={busy || !text.trim()} className="press label h-9 rounded-full bg-cream px-4 text-[10px] text-navy disabled:opacity-50">
            {busy ? 'Posting…' : 'Post'}
          </button>
        </div>
        <span className="label text-[9px] text-ice/80">
          {isPublic ? `Classmates see it with your ID (${me}).` : 'Only you see it.'} {text.length > MAX - 80 && `${MAX - text.length} left`}
        </span>
      </form>
      <ErrorText>{error}</ErrorText>

      {notes && notes.length === 0 && <p className="text-[14px] font-semibold text-ice">No notes yet.</p>}
      <ul className="flex flex-col gap-2">
        {notes?.map((n) => (
          <li key={n.id} className="flex flex-col gap-1.5 rounded-[18px] bg-navy-800 px-3.5 py-3">
            <span className="flex flex-wrap items-center gap-1.5">
              <span className="font-mono text-[12px] font-bold">{n.mine ? 'You' : n.author}</span>
              <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', n.isPublic ? 'bg-sky text-navy' : 'bg-navy text-ice')}>{n.isPublic ? 'Public' : 'Private'}</span>
              {n.date && <span className="label text-[9px] text-ice">For {label(n.date)}</span>}
              {n.hidden && <span className="label rounded-full bg-alert-ink px-2 py-0.5 text-[9px] text-cream">Hidden by staff</span>}
              {n.edited && <span className="label text-[9px] text-ice/70">Edited</span>}
            </span>
            {editing?.id === n.id ? (
              <form
                className="flex flex-col gap-2"
                onSubmit={(e) => {
                  e.preventDefault()
                  void act(async () => {
                    const updated = await api.put<Note>(`/me/notes/${n.id}`, { text: editing.text })
                    setNotes(notes.map((x) => (x.id === n.id ? updated : x)))
                    setEditing(null)
                  })
                }}
              >
                <textarea
                  aria-label="Edit note"
                  value={editing.text}
                  maxLength={MAX}
                  rows={2}
                  onChange={(e) => setEditing({ id: n.id, text: e.target.value })}
                  className="rounded-[12px] bg-cream p-3 text-[15px] font-semibold text-navy outline-none"
                />
                <span className="flex gap-3">
                  <button type="submit" className="label text-[10px] text-sky link-hover">
                    Save
                  </button>
                  <button type="button" className="label text-[10px] text-ice link-hover" onClick={() => setEditing(null)}>
                    Cancel
                  </button>
                </span>
              </form>
            ) : (
              <p className="text-[15px] leading-snug font-semibold break-words whitespace-pre-wrap">{n.text}</p>
            )}
            {editing?.id !== n.id && (
              <span className="flex gap-3">
                {n.mine ? (
                  <>
                    <button type="button" className="label text-[10px] text-ice link-hover" onClick={() => setEditing({ id: n.id, text: n.text })}>
                      Edit
                    </button>
                    <button
                      type="button"
                      className="label text-[10px] text-ice link-hover"
                      onClick={() =>
                        act(async () => {
                          await api.del(`/me/notes/${n.id}`)
                          setNotes(notes.filter((x) => x.id !== n.id))
                        })
                      }
                    >
                      Delete
                    </button>
                  </>
                ) : n.reported ? (
                  <span className="label text-[10px] text-ice/70">Reported</span>
                ) : (
                  <button
                    type="button"
                    className="label text-[10px] text-ice link-hover"
                    onClick={() =>
                      act(async () => {
                        await api.post(`/me/notes/${n.id}/report`)
                        setNotes(notes.map((x) => (x.id === n.id ? { ...x, reported: true } : x)))
                      })
                    }
                  >
                    Report
                  </button>
                )}
              </span>
            )}
          </li>
        ))}
      </ul>
    </div>
  )
}
