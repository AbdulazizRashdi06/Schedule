import { Fragment } from 'react'
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT, minutes } from '../lib/time'
import { SLOTS, TYPE_BADGE, TYPE_BG, TYPE_NAME, addDays, fmtDuration, fmtWeekRange, inWeek, nowMinutes, weekDays, weekStart, weekTitle, type ClassType, type Session } from '../lib/slots'
import { ErrorText, Sk, cx } from '../components/ui'
import { Icon, NeedsSection, RoundButton, useBack, useNow, useTimetable } from './timetable'

const ROW = 86
const GAP = 8

export default function Schedule() {
  const { me, tt, sessions: all, error } = useTimetable()
  const now = useNow()
  const back = useBack('/')
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()

  if (me && !me.setupDone) return <Navigate to="/setup" replace />

  const view = params.get('view') === 'day' ? 'day' : 'week'
  const offset = Number(params.get('w')) || 0
  const start = weekStart(now, offset)
  // Timetables change week to week: show this week's dated classes along with the weekly ones.
  const sessions = inWeek(all, start)
  const days = weekDays(sessions)
  const today = now.getDay()
  const thisWeek = offset === 0
  const dayParam = Number(params.get('day'))
  const day = params.has('day') && days.includes(dayParam) ? dayParam : thisWeek && days.includes(today) ? today : days[0]

  const go = (next: { view?: string; w?: number; day?: number }) => {
    const p = new URLSearchParams(params)
    for (const [k, v] of Object.entries(next)) p.set(k, String(v))
    setParams(p, { replace: true })
  }

  return (
    <div className="min-h-dvh bg-cream font-display text-navy">
      <div className="mx-auto max-w-md px-4 pt-[max(1rem,env(safe-area-inset-top))] pb-[max(1.5rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3">
          <RoundButton label="Back to today" onClick={back} className="border-[2.5px] border-navy text-navy">
            <Icon name="back" />
          </RoundButton>
          <div role="group" aria-label="View" className="label flex gap-0.5 rounded-full border-2 border-navy/15 p-1 text-[11px]">
            {(['day', 'week'] as const).map((v) => (
              <button
                key={v}
                type="button"
                aria-pressed={view === v}
                onClick={() => go({ view: v, day })}
                className={cx('press h-10 min-w-[72px] rounded-full px-4', view === v ? 'bg-navy text-cream' : 'text-muted tint')}
              >
                {v}
              </button>
            ))}
          </div>
        </header>

        <div className="mt-5 flex items-center justify-between gap-2">
          <RoundButton label="Previous week" onClick={() => go({ w: offset - 1 })} className="size-11 tint shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]">
            <Icon name="prev" />
          </RoundButton>
          <div className="min-w-0 text-center">
            <h1 className="display text-[30px] leading-[0.9] tracking-[-0.03em] text-navy">{weekTitle(offset)}</h1>
            <p className="label mt-1.5 text-[10px] text-muted">
              {fmtWeekRange(start, days.length)}
              {!thisWeek && (
                <button type="button" className="ml-2 text-navy link-hover" onClick={() => go({ w: 0 })}>
                  · Today
                </button>
              )}
            </p>
          </div>
          <RoundButton label="Next week" onClick={() => go({ w: offset + 1 })} className="size-11 bg-navy text-cream">
            <Icon name="next" />
          </RoundButton>
        </div>

        <div className="mt-6">
          {error ? (
            <ErrorText>{error}</ErrorText>
          ) : !me || !tt ? (
            <div aria-busy="true" aria-label="Loading your week" className="grid grid-cols-[38px_repeat(5,minmax(0,1fr))] gap-[5px]">
              {Array.from({ length: 36 }, (_, i) => (i % 6 === 0 ? <span key={i} /> : <Sk key={i} className="h-[86px] rounded-[14px]" />))}
            </div>
          ) : (
            <>
              <NeedsSection tt={tt} />
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
              className={cx('press mb-2 flex h-14 flex-col items-center justify-center rounded-[14px]', isToday ? 'bg-navy text-cream' : 'text-muted tint')}
            >
              <span className="label text-[9px]">{DAY_SHORT[d]}</span>
              <span className="display text-[20px] leading-tight">{date.getDate()}</span>
            </button>
          )
        })}
      </div>

      <div className="relative grid gap-x-[5px]" style={{ gridTemplateColumns: cols, gridTemplateRows: `repeat(${SLOTS.length}, ${ROW}px)`, rowGap: GAP }}>
        {SLOTS.map(([s, e], i) => (
          <div key={i} className="label flex flex-col justify-between py-1 text-[9px] leading-none tracking-[0.04em] text-muted" style={{ gridColumn: 1, gridRow: i + 1 }}>
            <span className="text-navy">{s}</span>
            <span>{e}</span>
          </div>
        ))}
        {days.map((d, c) => (
          <div
            key={d}
            className="rounded-[14px]"
            style={{ gridColumn: c + 2, gridRow: `1 / ${SLOTS.length + 1}`, background: `rgba(11,25,87,${d === today ? 0.08 : 0.035})` }}
          />
        ))}
        {/* A line at every break, across the whole week. */}
        {SLOTS.slice(1).map((_, i) => (
          <div key={i} className="pointer-events-none absolute right-0 left-[43px] h-px bg-navy/10" style={{ top: (i + 1) * (ROW + GAP) - GAP / 2 }} />
        ))}
        {lineY !== null && (
          <div className="pointer-events-none absolute right-0 left-[38px] z-10 h-0.5 rounded-full bg-navy" style={{ top: lineY - 1 }} aria-hidden="true">
            <span className="absolute -top-[3px] -left-1 size-2 rounded-full bg-navy" />
          </div>
        )}
        {days.map((d, c) =>
          SLOTS.map((_, slot) => {
            const cell = sessions.filter((s) => s.meeting.day === d && s.slot === slot)
            if (cell.length === 0) return null
            return (
              <div key={`${d}-${slot}`} className="relative z-20 flex min-w-0 gap-0.5" style={{ gridColumn: c + 2, gridRow: `${slot + 1} / span ${Math.max(...cell.map((s) => s.span))}` }}>
                {cell.map((s, i) => {
                  const live = d === today && s.start <= nowMin && nowMin < s.end
                  return (
                    <button
                      key={i}
                      type="button"
                      onClick={() => onDay(d)}
                      aria-label={blockLabel(s)}
                      className={cx(
                        'press relative flex min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] p-1.5 text-left shadow-[0_0_0_1.5px_rgb(11_25_87/0.12)]',
                        TYPE_BG[s.type],
                        live && 'ring-2 ring-navy ring-offset-2 ring-offset-cream',
                        s.clash && 'outline-2 -outline-offset-2 outline-alert',
                      )}
                    >
                      <span className={cx('display leading-none tracking-[-0.03em]', cell.length > 1 ? 'text-[12px] break-all' : 'truncate text-[16px]')}>{s.cls.code}</span>
                      <span className="label mt-1 text-[9px] tracking-[0.04em]">{s.meeting.start}
                        {s.span > 1 && `–${s.meeting.end}`}
                      </span>
                      {cell.length === 1 && <span className="mt-auto truncate text-[11px] font-semibold">{s.meeting.room}</span>}
                      {s.cls.changed && <span className="absolute top-1.5 right-1.5 size-1.5 rounded-full bg-current" />}
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
      <h2 className="display text-[60px] leading-[0.86] tracking-[-0.06em] text-navy">{DAY_LONG[day]}</h2>
      <p className="label mt-2 text-[10px] text-muted">
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
              className={cx('press flex h-[76px] flex-col items-center justify-center rounded-[20px]', selected ? 'bg-navy text-cream' : 'tint text-navy shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
            >
              <span className="label text-[9px]">{d === today ? 'Today' : DAY_SHORT[d]}</span>
              <span className="display text-[22px] leading-tight">{addDays(start, d).getDate()}</span>
              <span className={cx('label text-[9px]', selected ? 'text-cream/70' : 'text-muted')}>{n} cls</span>
            </button>
          )
        })}
      </div>

      <div className="mt-6 space-y-3" role="tabpanel">
        {list.length === 0 && <p className="rounded-[20px] border border-dashed border-navy/20 p-6 text-center text-[15px] font-semibold text-muted">No classes on {DAY_LONG[day]}.</p>}
        {list.map((s, i) => {
          const gap = i > 0 ? s.start - list[i - 1].end : 0
          const live = day === today && s.start <= nowMin && nowMin < s.end
          return (
            <Fragment key={i}>
              {gap >= 30 && (
                <div className="label ml-[68px] flex h-11 items-center justify-center rounded-[18px] border border-dashed border-navy/20 text-[10px] text-muted">
                  Free · {fmtDuration(gap)}
                </div>
              )}
              <div className="flex gap-3">
                <div className="label w-14 shrink-0 pt-4 text-right">
                  <div className="text-[12px] text-navy">{s.meeting.start}</div>
                  <div className="mt-1 text-[10px] text-muted">{s.meeting.end}</div>
                </div>
                <button
                  type="button"
                  onClick={() => onOpen(s)}
                  className={cx('press min-w-0 flex-1 rounded-[20px] p-4 text-left shadow-[0_0_0_1.5px_rgb(11_25_87/0.12)]', TYPE_BG[s.type], live && 'ring-2 ring-navy ring-offset-2 ring-offset-cream')}
                >
                  <span className="flex flex-wrap items-center gap-1.5">
                    <span className="label text-[10px]">
                      {TYPE_NAME[s.type]} · {s.cls.code}
                    </span>
                    {live && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', TYPE_BADGE[s.type])}>● Now</span>}
                    {s.cls.changed && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', TYPE_BADGE[s.type])}>Changed</span>}
                    {s.clash && <span className="label rounded-full bg-alert-ink px-2 py-0.5 text-[9px] text-cream">Clash</span>}
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
    <div className="mt-8 flex items-center justify-center gap-5 border-t border-navy/10 pt-5">
      {items.map((t) => (
        <span key={t} className="label flex items-center gap-2 text-[10px] text-muted">
          <span className={cx('size-3 rounded-[4px] shadow-[0_0_0_1.5px_rgb(11_25_87/0.2)]', TYPE_BG[t])} />
          {TYPE_NAME[t]}
        </span>
      ))}
    </div>
  )
}
