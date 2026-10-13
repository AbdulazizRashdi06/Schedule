import { Fragment } from 'react'
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT, minutes } from '../lib/time'
import { SLOTS, TYPE_BG, TYPE_NAME, addDays, fmtDuration, fmtWeekRange, nowMinutes, weekDays, weekStart, weekTitle, type ClassType, type Session } from '../lib/slots'
import { ErrorText, Loading, cx } from '../components/ui'
import { Icon, NeedsSection, RoundButton, useBack, useNow, useTimetable } from './timetable'

const ROW = 86
const GAP = 8

export default function Schedule() {
  const { me, tt, sessions, error } = useTimetable()
  const now = useNow()
  const back = useBack('/')
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()

  if (me && !me.setupDone) return <Navigate to="/setup" replace />

  const view = params.get('view') === 'day' ? 'day' : 'week'
  const offset = Number(params.get('w')) || 0
  const days = weekDays(sessions)
  const today = now.getDay()
  const thisWeek = offset === 0
  const dayParam = Number(params.get('day'))
  const day = params.has('day') && days.includes(dayParam) ? dayParam : thisWeek && days.includes(today) ? today : days[0]
  const start = weekStart(now, offset)

  const go = (next: { view?: string; w?: number; day?: number }) => {
    const p = new URLSearchParams(params)
    for (const [k, v] of Object.entries(next)) p.set(k, String(v))
    setParams(p, { replace: true })
  }

  return (
    <div className="min-h-dvh bg-navy font-display text-cream">
      <div className="mx-auto max-w-md px-4 pt-[max(1rem,env(safe-area-inset-top))] pb-[max(1.5rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3">
          <RoundButton label="Back" onClick={back} className="bg-navy-800 text-cream hover:bg-navy-700">
            <Icon name="back" />
          </RoundButton>
          <div className="label flex rounded-full bg-navy-800 p-1 text-[11px]">
            {(['day', 'week'] as const).map((v) => (
              <button
                key={v}
                type="button"
                aria-pressed={view === v}
                onClick={() => go({ view: v, day })}
                className={cx('h-10 min-w-[72px] rounded-full px-4 transition', view === v ? 'bg-cream text-navy' : 'text-ice hover:text-cream')}
              >
                {v}
              </button>
            ))}
          </div>
        </header>

        <div className="mt-5 flex items-center justify-between gap-2">
          <RoundButton label="Previous week" onClick={() => go({ w: offset - 1 })} className="bg-navy-800 hover:bg-navy-700">
            <Icon name="prev" />
          </RoundButton>
          <div className="min-w-0 text-center">
            <h1 className="display text-[30px] leading-none tracking-[-0.03em]">{weekTitle(offset)}</h1>
            <p className="label mt-1.5 text-[10px] text-ice">
              {fmtWeekRange(start, days.length)}
              {!thisWeek && (
                <button type="button" className="ml-2 text-sky underline-offset-2 hover:underline" onClick={() => go({ w: 0 })}>
                  · Today
                </button>
              )}
            </p>
          </div>
          <RoundButton label="Next week" onClick={() => go({ w: offset + 1 })} className="bg-navy-800 hover:bg-navy-700">
            <Icon name="next" />
          </RoundButton>
        </div>

        <div className="mt-6">
          {error ? (
            <ErrorText>{error}</ErrorText>
          ) : !me || !tt ? (
            <div className="text-ice">
              <Loading />
            </div>
          ) : (
            <>
              <NeedsSection tt={tt} dark />
              {view === 'week' ? (
                <WeekGrid
                  days={days}
                  sessions={sessions}
                  start={start}
                  today={thisWeek ? today : -1}
                  nowMin={nowMinutes(now)}
                  onDay={(d) => go({ view: 'day', day: d })}
                />
              ) : (
                <DayView
                  days={days}
                  day={day}
                  sessions={sessions}
                  start={start}
                  today={thisWeek ? today : -1}
                  nowMin={nowMinutes(now)}
                  onDay={(d) => go({ day: d })}
                  onOpen={(s) => navigate(`/course/${s.cls.courseId}`)}
                />
              )}
              <Legend />
            </>
          )}
        </div>
      </div>
    </div>
  )
}

const blockLabel = (s: Session) =>
  `${s.cls.name}, ${TYPE_NAME[s.type].toLowerCase()}, ${DAY_LONG[s.meeting.day]} ${s.meeting.start} to ${s.meeting.end}${s.meeting.room ? `, room ${s.meeting.room}` : ''}${s.clash ? ', clashes with another class' : ''}${s.cls.changed ? ', changed' : ''}`

/** Where the current time sits on the slot grid, or null outside the teaching day. */
function nowY(nowMin: number) {
  for (let i = 0; i < SLOTS.length; i++) {
    const s = minutes(SLOTS[i][0])
    const e = minutes(SLOTS[i][1])
    const top = i * (ROW + GAP)
    if (nowMin >= s && nowMin < e) return top + ((nowMin - s) / (e - s)) * ROW
    const nextStart = i + 1 < SLOTS.length ? minutes(SLOTS[i + 1][0]) : null
    if (nextStart !== null && nowMin >= e && nowMin < nextStart) return top + ROW + ((nowMin - e) / (nextStart - e)) * GAP
  }
  return null
}

function WeekGrid({ days, sessions, start, today, nowMin, onDay }: { days: number[]; sessions: Session[]; start: Date; today: number; nowMin: number; onDay: (d: number) => void }) {
  const cols = `38px repeat(${days.length}, minmax(0, 1fr))`
  const lineY = today >= 0 && days.includes(today) ? nowY(nowMin) : null
  return (
    <div>
      <div className="grid gap-x-[5px]" style={{ gridTemplateColumns: cols }}>
        <div />
        {days.map((d) => {
          const date = addDays(start, d)
          const isToday = d === today
          return (
            <button
              key={d}
              type="button"
              onClick={() => onDay(d)}
              aria-label={`${DAY_LONG[d]} ${date.getDate()}, open day view`}
              className={cx('mb-2 flex h-14 flex-col items-center justify-center rounded-[14px] transition', isToday ? 'bg-cream text-navy' : 'text-ice hover:bg-navy-800')}
            >
              <span className="label text-[9px]">{DAY_SHORT[d]}</span>
              <span className="display text-[20px] leading-tight">{date.getDate()}</span>
            </button>
          )
        })}
      </div>

      <div className="relative grid gap-x-[5px]" style={{ gridTemplateColumns: cols, gridTemplateRows: `repeat(${SLOTS.length}, ${ROW}px)`, rowGap: GAP }}>
        {SLOTS.map(([s, e], i) => (
          <div key={i} className="label flex flex-col justify-between py-1 text-[9px] leading-none tracking-[0.04em] text-ice/80" style={{ gridColumn: 1, gridRow: i + 1 }}>
            <span className="text-cream">{s}</span>
            <span>{e}</span>
          </div>
        ))}
        {days.map((d, c) => (
          <div
            key={d}
            className="rounded-[14px]"
            style={{ gridColumn: c + 2, gridRow: `1 / ${SLOTS.length + 1}`, background: `rgba(158,204,250,${d === today ? 0.14 : 0.06})` }}
          />
        ))}
        {/* A line at every break, across the whole week. */}
        {SLOTS.slice(1).map((_, i) => (
          <div key={i} className="pointer-events-none absolute right-0 left-[43px] h-px bg-navy-line-2" style={{ top: (i + 1) * (ROW + GAP) - GAP / 2 }} />
        ))}
        {lineY !== null && (
          <div className="pointer-events-none absolute right-0 left-[38px] z-10 h-0.5 rounded-full bg-cream" style={{ top: lineY - 1 }} aria-hidden="true">
            <span className="absolute -top-[3px] -left-1 size-2 rounded-full bg-cream" />
          </div>
        )}
        {days.map((d, c) =>
          SLOTS.map((_, slot) => {
            const cell = sessions.filter((s) => s.meeting.day === d && s.slot === slot)
            if (cell.length === 0) return null
            return (
              <div key={`${d}-${slot}`} className="relative z-20 flex min-w-0 gap-0.5" style={{ gridColumn: c + 2, gridRow: slot + 1 }}>
                {cell.map((s, i) => {
                  const live = d === today && s.start <= nowMin && nowMin < s.end
                  return (
                    <button
                      key={i}
                      type="button"
                      onClick={() => onDay(d)}
                      aria-label={blockLabel(s)}
                      className={cx(
                        'relative flex min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] p-1.5 text-left text-navy transition active:scale-[0.97]',
                        TYPE_BG[s.type],
                        live && 'ring-2 ring-cream ring-offset-2 ring-offset-navy',
                        s.clash && 'outline-2 -outline-offset-2 outline-red-500',
                      )}
                    >
                      <span className={cx('display leading-none tracking-[-0.03em]', cell.length > 1 ? 'text-[12px] break-all' : 'truncate text-[16px]')}>{s.cls.code}</span>
                      <span className="label mt-1 text-[9px] tracking-[0.04em]">{s.meeting.start}</span>
                      {cell.length === 1 && <span className="mt-auto truncate text-[11px] font-semibold">{s.meeting.room}</span>}
                      {s.cls.changed && <span className="absolute top-1.5 right-1.5 size-1.5 rounded-full bg-navy" />}
                    </button>
                  )
                })}
              </div>
            )
          }),
        )}
      </div>
    </div>
  )
}

function DayView({ days, day, sessions, start, today, nowMin, onDay, onOpen }: { days: number[]; day: number; sessions: Session[]; start: Date; today: number; nowMin: number; onDay: (d: number) => void; onOpen: (s: Session) => void }) {
  const list = sessions.filter((s) => s.meeting.day === day)
  const total = list.reduce((sum, s) => sum + (s.end - s.start), 0)
  return (
    <div>
      <h2 className="display text-[60px] leading-[0.9] tracking-[-0.05em]">{DAY_LONG[day]}.</h2>
      <p className="label mt-2 text-[10px] text-ice">
        {list.length === 0
          ? 'No classes'
          : `${list.length} ${list.length === 1 ? 'class' : 'classes'} · ${fmtDuration(total)} · ${list[0].meeting.start}–${list[list.length - 1].meeting.end}`}
      </p>

      <div className="mt-5 grid gap-1.5" role="tablist" aria-label="Days" style={{ gridTemplateColumns: `repeat(${days.length}, minmax(0, 1fr))` }}>
        {days.map((d) => {
          const n = sessions.filter((s) => s.meeting.day === d).length
          const selected = d === day
          return (
            <button
              key={d}
              type="button"
              role="tab"
              aria-selected={selected}
              onClick={() => onDay(d)}
              className={cx('flex h-[76px] flex-col items-center justify-center rounded-[20px] transition', selected ? 'bg-cream text-navy' : 'bg-navy-800 text-cream hover:bg-navy-700')}
            >
              <span className="label text-[9px]">{d === today ? 'Today' : DAY_SHORT[d]}</span>
              <span className="display text-[22px] leading-tight">{addDays(start, d).getDate()}</span>
              <span className={cx('label text-[9px]', selected ? 'text-navy/60' : 'text-ice/80')}>{n} cls</span>
            </button>
          )
        })}
      </div>

      <div className="mt-6 space-y-3" role="tabpanel">
        {list.length === 0 && <p className="rounded-[20px] border border-dashed border-navy-line-2 p-6 text-center text-[15px] font-semibold text-ice">No classes on {DAY_LONG[day]}.</p>}
        {list.map((s, i) => {
          const gap = i > 0 ? s.start - list[i - 1].end : 0
          const live = day === today && s.start <= nowMin && nowMin < s.end
          return (
            <Fragment key={i}>
              {gap >= 30 && (
                <div className="label ml-[68px] flex h-11 items-center justify-center rounded-[18px] border border-dashed border-navy-line-2 text-[10px] text-ice">
                  Free · {fmtDuration(gap)}
                </div>
              )}
              <div className="flex gap-3">
                <div className="label w-14 shrink-0 pt-4 text-right">
                  <div className="text-[12px] text-cream">{s.meeting.start}</div>
                  <div className="mt-1 text-[10px] text-ice/80">{s.meeting.end}</div>
                </div>
                <button
                  type="button"
                  onClick={() => onOpen(s)}
                  className={cx('min-w-0 flex-1 rounded-[20px] p-4 text-left text-navy transition active:scale-[0.99]', TYPE_BG[s.type], live && 'ring-2 ring-cream ring-offset-2 ring-offset-navy')}
                >
                  <span className="flex flex-wrap items-center gap-1.5">
                    <span className="label text-[10px]">
                      {TYPE_NAME[s.type]} · {s.cls.code}
                    </span>
                    {live && <span className="label rounded-full bg-navy px-2 py-0.5 text-[9px] text-cream">● Now</span>}
                    {s.cls.changed && <span className="label rounded-full bg-navy px-2 py-0.5 text-[9px] text-cream">Changed</span>}
                    {s.clash && <span className="label rounded-full bg-red-600 px-2 py-0.5 text-[9px] text-white">Clash</span>}
                  </span>
                  <span className="display mt-1.5 block text-[20px] leading-[1.1] tracking-[-0.02em] break-words hyphens-auto">{s.cls.name}</span>
                  <span className="mt-1 flex items-center gap-1 text-[13px] font-semibold">
                    <Icon name="pin" className="size-3.5" />
                    {s.meeting.room || 'Room TBA'}
                  </span>
                </button>
              </div>
            </Fragment>
          )
        })}
      </div>
    </div>
  )
}

function Legend() {
  const items: ClassType[] = ['lecture', 'lab', 'tutorial']
  return (
    <div className="mt-8 flex items-center justify-center gap-5 border-t border-navy-line pt-5">
      {items.map((t) => (
        <span key={t} className="label flex items-center gap-2 text-[10px] text-ice">
          <span className={cx('size-3 rounded-[4px]', TYPE_BG[t])} />
          {TYPE_NAME[t]}
        </span>
      ))}
    </div>
  )
}
