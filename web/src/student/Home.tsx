import { useEffect, useMemo, useState } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { api, errorText, store } from '../lib/api'
import type { Me, Meeting, Timetable, TimetableClass } from '../lib/types'
import { DAY_LONG, DAY_SHORT, fmtTime, minutes } from '../lib/time'
import { Badge, Card, ErrorText, Loading, Notice, Sheet, cx } from '../components/ui'
import { SectionDetails } from '../components/CourseRow'
import { StudentShell } from './StudentShell'

interface Slot {
  cls: TimetableClass
  meeting: Meeting
  clash: boolean
}

function useNow() {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(t)
  }, [])
  return now
}

export default function Home() {
  const [me, setMe] = useState<Me | null>(null)
  const [tt, setTt] = useState<Timetable | null>(null)
  const [error, setError] = useState('')
  const [mode, setMode] = useState<'day' | 'week'>(() => (store.get('tt-mode') === 'week' ? 'week' : 'day'))
  const [open, setOpen] = useState<TimetableClass | null>(null)
  const now = useNow()
  const today = now.getDay()
  const [day, setDay] = useState(today)

  useEffect(() => {
    api.get<Me>('/me').then(async (m) => {
      setMe(m)
      if (m.setupDone) setTt(await api.get<Timetable>('/me/timetable'))
    }).catch((e) => setError(errorText(e)))
  }, [])

  const slots = useMemo(() => {
    if (!tt) return [] as Slot[]
    const clashing = new Set(tt.clashes.flat())
    const out: Slot[] = []
    for (const c of tt.classes) for (const m of c.section.meetings) out.push({ cls: c, meeting: m, clash: false })
    // Only meetings that actually overlap get the ⚠, not every meeting of a clashing section.
    for (const a of out) {
      if (!clashing.has(a.cls.section.id)) continue
      a.clash = out.some((b) => b !== a && b.cls.courseId !== a.cls.courseId && b.meeting.day === a.meeting.day && minutes(a.meeting.start) < minutes(b.meeting.end) && minutes(b.meeting.start) < minutes(a.meeting.end))
    }
    return out.sort((a, b) => a.meeting.day - b.meeting.day || minutes(a.meeting.start) - minutes(b.meeting.start))
  }, [tt])

  if (me && !me.setupDone) return <Navigate to="/setup" replace />
  if (error) return <StudentShell><ErrorText>{error}</ErrorText></StudentShell>
  if (!me || !tt) return <StudentShell><Loading /></StudentShell>

  const usedDays = new Set(slots.map((s) => s.meeting.day))
  // Sun–Thu always; Fri and Sat only when a class meets then.
  const days = [0, 1, 2, 3, 4, 5, 6].filter((d) => d <= 4 || usedDays.has(d))

  const openClass = (c: TimetableClass) => {
    setOpen(c)
    if (c.changed) {
      api.post('/me/seen', { sectionId: c.section.id }).catch(() => {})
      setTt((t) => t && { ...t, classes: t.classes.map((x) => (x.section.id === c.section.id ? { ...x, changed: false } : x)) })
    }
  }

  return (
    <StudentShell
      setupDone
      title={
        <div>
          <div className="text-lg font-semibold leading-tight">My timetable</div>
          <div className="text-xs text-zinc-500">{tt.semester?.name}</div>
        </div>
      }
    >
      <div className="mb-4 grid grid-cols-2 rounded-xl bg-zinc-200/70 p-1 text-sm font-medium dark:bg-zinc-800/70" role="tablist">
        {(['day', 'week'] as const).map((m) => (
          <button
            key={m}
            role="tab"
            aria-selected={mode === m}
            onClick={() => {
              setMode(m)
              store.set('tt-mode', m)
            }}
            className={cx('h-9 rounded-lg transition', mode === m ? 'bg-white shadow-sm dark:bg-zinc-950' : 'text-zinc-600 dark:text-zinc-400')}
          >
            {m === 'day' ? 'Day' : 'Week'}
          </button>
        ))}
      </div>

      {tt.needsSection.length > 0 && (
        <div className="mb-4">
          <Notice tone="warn">
            {tt.needsSection.some((n) => n.removed) ? 'A section you picked was removed: ' : 'No section picked for '}
            <b>{tt.needsSection.map((n) => n.code).join(', ')}</b>.{' '}
            <Link to="/edit" className="font-medium underline">Pick again</Link>
          </Notice>
        </div>
      )}

      {tt.classes.length === 0 ? (
        <Notice>
          No classes yet. <Link to="/edit" className="font-medium underline">Add your courses</Link>
        </Notice>
      ) : mode === 'day' ? (
        <DayView days={days} day={day} setDay={setDay} today={today} now={now} slots={slots} onOpen={openClass} />
      ) : (
        <WeekView days={days} slots={slots} today={today} onOpen={openClass} />
      )}

      <div className="h-10" />
      <Sheet open={!!open} onClose={() => setOpen(null)} title={open && `${open.code} · Section ${open.section.number}`}>
        {open && (
          <div className="space-y-4">
            {open.section.changeSummary && open.section.changedAt && (
              <Notice tone="warn">
                <div className="font-medium">Changed {new Date(open.section.changedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</div>
                <div>{open.section.changeSummary}</div>
              </Notice>
            )}
            <SectionDetails course={open} section={open.section} />
          </div>
        )}
      </Sheet>
    </StudentShell>
  )
}

function DayView({ days, day, setDay, today, now, slots, onOpen }: { days: number[]; day: number; setDay: (d: number) => void; today: number; now: Date; slots: Slot[]; onOpen: (c: TimetableClass) => void }) {
  const list = slots.filter((s) => s.meeting.day === day)
  const nowMin = now.getHours() * 60 + now.getMinutes()
  const isToday = day === today
  // The class happening now, or the next one today.
  const highlight = isToday ? list.find((s) => minutes(s.meeting.end) > nowMin) : undefined
  // Next day (after today) that has classes, for the "no classes today" preview.
  const nextDay = [1, 2, 3, 4, 5, 6, 7].map((i) => (day + i) % 7).find((d) => slots.some((s) => s.meeting.day === d))

  return (
    <>
      <div className="-mx-5 mb-4 flex gap-1.5 overflow-x-auto px-5 pb-1" role="tablist">
        {days.map((d) => (
          <button
            key={d}
            role="tab"
            aria-selected={d === day}
            onClick={() => setDay(d)}
            className={cx(
              'relative flex h-14 min-w-14 flex-1 flex-col items-center justify-center rounded-xl text-sm font-medium ring-1 transition',
              d === day ? 'bg-indigo-600 text-white ring-indigo-600 dark:bg-indigo-500 dark:ring-indigo-500' : 'bg-white ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800',
            )}
          >
            {DAY_SHORT[d]}
            <span className={cx('text-[10px]', d === day ? 'text-indigo-100' : 'text-zinc-400')}>{d === today ? 'Today' : `${slots.filter((s) => s.meeting.day === d).length || '–'}`}</span>
          </button>
        ))}
      </div>

      {list.length === 0 ? (
        <div className="space-y-4">
          <div className="rounded-2xl bg-white p-6 text-center ring-1 ring-zinc-200/80 dark:bg-zinc-900 dark:ring-zinc-800">
            <div className="text-3xl">🌤️</div>
            <div className="mt-2 font-medium">{isToday ? 'No classes today' : `No classes on ${DAY_LONG[day]}`}</div>
          </div>
          {nextDay !== undefined && (
            <div>
              <div className="mb-2 flex items-center justify-between">
                <h2 className="text-sm font-semibold text-zinc-500">Next: {DAY_LONG[nextDay]}</h2>
                {days.includes(nextDay) && (
                  <button className="text-sm font-medium text-indigo-600 dark:text-indigo-400" onClick={() => setDay(nextDay)}>
                    Open
                  </button>
                )}
              </div>
              <div className="space-y-2.5 opacity-80">
                {slots.filter((s) => s.meeting.day === nextDay).map((s, i) => (
                  <ClassCard key={i} slot={s} onOpen={onOpen} />
                ))}
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="space-y-2.5">
          {list.map((s, i) => {
            const past = isToday && minutes(s.meeting.end) <= nowMin
            const live = isToday && minutes(s.meeting.start) <= nowMin && nowMin < minutes(s.meeting.end)
            return <ClassCard key={i} slot={s} onOpen={onOpen} past={past} highlight={s === highlight} label={s === highlight ? (live ? 'Now' : 'Next') : undefined} />
          })}
          {isToday && !highlight && <p className="pt-2 text-center text-sm text-zinc-500">That's it for today 🎉</p>}
        </div>
      )}
    </>
  )
}

function ClassCard({ slot, onOpen, past, highlight, label }: { slot: Slot; onOpen: (c: TimetableClass) => void; past?: boolean; highlight?: boolean; label?: string }) {
  const { cls, meeting } = slot
  return (
    <Card
      onClick={() => onOpen(cls)}
      className={cx('flex gap-3.5 p-3.5', past && 'opacity-50', highlight && 'ring-2! ring-indigo-600! dark:ring-indigo-400!')}
    >
      <div className="w-16 shrink-0 text-right">
        <div className="text-[15px] font-semibold tabular-nums">{fmtTime(meeting.start).replace(' ', ' ')}</div>
        <div className="text-xs text-zinc-500 tabular-nums">{fmtTime(meeting.end)}</div>
      </div>
      <div className={cx('w-1 shrink-0 rounded-full', typeColor(cls.type))} />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="font-semibold">{cls.code}</span>
          {label && <Badge tone="indigo">{label}</Badge>}
          {cls.changed && <Badge tone="amber">Changed</Badge>}
          {slot.clash && <Badge tone="red">⚠ Clash</Badge>}
        </div>
        <div className="truncate text-[15px] text-zinc-600 dark:text-zinc-400">{cls.name}</div>
        <div className="mt-0.5 truncate text-[13px] text-zinc-500">
          {meeting.room || 'Room TBA'} · {cls.section.instructor || 'Instructor TBA'}
        </div>
      </div>
    </Card>
  )
}

const typeColor = (t: string) => (t === 'Core' ? 'bg-indigo-500' : t === 'LS' ? 'bg-emerald-500' : 'bg-amber-500')
const blockColor = (t: string) =>
  t === 'Core'
    ? 'bg-indigo-50 text-indigo-950 ring-indigo-200 dark:bg-indigo-950/70 dark:text-indigo-100 dark:ring-indigo-800'
    : t === 'LS'
      ? 'bg-emerald-50 text-emerald-950 ring-emerald-200 dark:bg-emerald-950/60 dark:text-emerald-100 dark:ring-emerald-800'
      : 'bg-amber-50 text-amber-950 ring-amber-200 dark:bg-amber-950/50 dark:text-amber-100 dark:ring-amber-800'

/** Days × times grid. Overlapping classes sit side by side. Scrolls sideways on a phone. */
function WeekView({ days, slots, today, onOpen }: { days: number[]; slots: Slot[]; today: number; onOpen: (c: TimetableClass) => void }) {
  const PX = 1.1 // pixels per minute
  const startMin = Math.floor(Math.min(...slots.map((s) => minutes(s.meeting.start))) / 60) * 60
  const endMin = Math.ceil(Math.max(...slots.map((s) => minutes(s.meeting.end))) / 60) * 60
  const hours = Array.from({ length: (endMin - startMin) / 60 + 1 }, (_, i) => startMin + i * 60)
  const height = (endMin - startMin) * PX

  // Lanes per day so overlapping blocks share the column width.
  const lanes = new Map<Slot, { lane: number; of: number }>()
  for (const d of days) {
    const list = slots.filter((s) => s.meeting.day === d)
    const groups: Slot[][] = []
    for (const s of list) {
      const g = groups.find((grp) => grp.some((o) => minutes(o.meeting.start) < minutes(s.meeting.end) && minutes(s.meeting.start) < minutes(o.meeting.end)))
      if (g) g.push(s)
      else groups.push([s])
    }
    for (const g of groups) g.forEach((s, i) => lanes.set(s, { lane: i, of: g.length }))
  }

  return (
    <div className="-mx-5 overflow-x-auto px-5">
      <div>
        <div className="grid" style={{ gridTemplateColumns: `2.5rem repeat(${days.length}, minmax(4rem, 1fr))` }}>
          <div />
          {days.map((d) => (
            <div key={d} className={cx('pb-2 text-center text-sm font-medium', d === today ? 'text-indigo-600 dark:text-indigo-400' : 'text-zinc-500')}>
              {DAY_SHORT[d]}
            </div>
          ))}
          <div className="relative" style={{ height }}>
            {hours.map((h) => (
              <div key={h} className="absolute right-1.5 -translate-y-1/2 text-[10px] text-zinc-400 tabular-nums" style={{ top: (h - startMin) * PX }}>
                {fmtTime(`${Math.floor(h / 60)}:00`).replace(':00 ', '')}
              </div>
            ))}
          </div>
          {days.map((d) => (
            <div key={d} className={cx('relative border-l border-zinc-200 dark:border-zinc-800', d === today && 'bg-indigo-50/40 dark:bg-indigo-950/20')} style={{ height }}>
              {hours.map((h) => (
                <div key={h} className="absolute inset-x-0 border-t border-dashed border-zinc-200 dark:border-zinc-800" style={{ top: (h - startMin) * PX }} />
              ))}
              {slots.filter((s) => s.meeting.day === d).map((s, i) => {
                const l = lanes.get(s)!
                const top = (minutes(s.meeting.start) - startMin) * PX
                const h = (minutes(s.meeting.end) - minutes(s.meeting.start)) * PX
                return (
                  <button
                    key={i}
                    onClick={() => onOpen(s.cls)}
                    className={cx('absolute overflow-hidden rounded-lg p-1 text-left ring-1 transition hover:brightness-95', blockColor(s.cls.type), s.clash && 'ring-2 ring-red-400 dark:ring-red-500')}
                    style={{ top: top + 1, height: h - 2, left: `calc(${(l.lane / l.of) * 100}% + 2px)`, width: `calc(${100 / l.of}% - 4px)` }}
                  >
                    <div className="flex items-center gap-0.5 text-[11px] leading-tight font-semibold">
                      {s.clash && <span>⚠</span>}
                      <span className="truncate">{s.cls.code}</span>
                    </div>
                    <div className="truncate text-[10px] leading-tight opacity-80">{s.meeting.room}</div>
                    {s.cls.changed && <div className="mt-0.5 inline-block rounded bg-amber-400 px-1 text-[9px] font-semibold text-amber-950">Changed</div>}
                  </button>
                )
              })}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
