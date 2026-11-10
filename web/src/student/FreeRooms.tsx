import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { DAY_SHORT } from '../lib/time'
import { SLOTS, fmtDate } from '../lib/slots'
import { ErrorText, Loading, Notice, cx } from '../components/ui'
import { BackButton, useBack, useNow } from './timetable'

interface FreeRoomsReply {
  date: string
  at: string
  classesThatDay: boolean
  /** The holiday's name when the day is a day off. */
  dayOff: string | null
  busy: number
  rooms: { room: string; freeUntil: string | null }[]
}

/**
 * Rooms with no class right now, or at the start of a class slot today, each with how long it
 * stays free. From the timetable only: events and exams aren't in it, so a "free" room can still be in use.
 */
export default function FreeRooms() {
  const back = useBack('/')
  const now = useNow()
  // null = now; otherwise a slot's start time today.
  const [at, setAt] = useState<string | null>(null)
  const [reply, setReply] = useState<FreeRoomsReply | null>(null)
  const [error, setError] = useState('')
  // "Now" follows the clock (useNow ticks every 30 seconds); a picked slot stays put.
  const minute = at ?? `${now.getHours()}:${now.getMinutes()}`

  useEffect(() => {
    let live = true
    api.get<FreeRoomsReply>(`/rooms/free${at ? `?at=${at}` : ''}`).then(
      (r) => live && (setReply(r), setError('')),
      (e) => live && setError(errorText(e)),
    )
    return () => {
      live = false
    }
  }, [at, minute])

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-6 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3 pt-1">
          <BackButton onClick={back} />
          <span className="label text-[12px] tracking-[0.14em]">
            {DAY_SHORT[now.getDay()]} {fmtDate(now)}
          </span>
        </header>

        <h1 className="display mt-6 text-[60px] leading-[0.86] tracking-[-0.05em]">Free rooms.</h1>

        {/* Now, or the start of any class slot today. */}
        <div role="radiogroup" aria-label="When" className="-mx-6 mt-5 flex gap-1.5 overflow-x-auto px-6 pb-1 [scrollbar-width:none]">
          {[null, ...SLOTS.map(([s]) => s)].map((t) => (
            <button
              key={t ?? 'now'}
              type="button"
              role="radio"
              aria-checked={at === t}
              onClick={() => setAt(t)}
              className={cx(
                'press label h-10 shrink-0 rounded-full px-4 text-[12px]',
                at === t ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.2)]',
              )}
            >
              {t ?? 'Now'}
            </button>
          ))}
        </div>

        <Notice className="mt-4">
          Free of classes only. Events and exams aren’t in the timetable, so a room here could still be in use.
        </Notice>

        <ErrorText className="mt-4">{error}</ErrorText>

        {!reply && !error ? (
          <Loading rows={5} label="Finding free rooms" />
        ) : (
          reply && (
            <>
              <p className="label mt-5 text-[12px] text-muted">
                {reply.rooms.length} free · {reply.busy} in class · at {reply.at}
              </p>
              {!reply.classesThatDay && (
                <p className="mt-2 text-[15px] font-semibold text-muted">
                  {reply.dayOff ? `${reply.dayOff} is a day off, so every room shows as free.` : 'No classes on this day, so every room shows as free.'} Check the building is open.
                </p>
              )}
              {reply.rooms.length === 0 && reply.classesThatDay && (
                <p className="mt-6 text-[17px] font-semibold">Every room has a class right now. Try the next slot.</p>
              )}
              {reply.rooms.length > 0 && (
                <ul aria-label="Available rooms" className="mt-3 border-t-[2.5px] border-navy">
                  {reply.rooms.map((r) => (
                    <li key={r.room} className="flex items-baseline justify-between gap-3 border-b-[1.5px] border-navy/10 py-3">
                      <span className="display text-[24px] leading-none tracking-[-0.03em]">{r.room}</span>
                      <span className={cx('label text-[11px]', r.freeUntil ? 'text-navy' : 'text-muted')}>
                        {r.freeUntil ? `Until ${r.freeUntil}` : 'Rest of the day'}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )
        )}
      </div>
    </div>
  )
}
