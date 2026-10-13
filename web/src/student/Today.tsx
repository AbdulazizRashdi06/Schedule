import { useState, type ReactNode } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT } from '../lib/time'
import { TYPE_NAME, addDays, clock, fmtDate, fmtDuration, nowMinutes, onDate, weekStart, type Session } from '../lib/slots'
import { Button, ErrorText, Sk, cx } from '../components/ui'
import { MenuSheet } from './StudentShell'
import { Icon, NeedsSection, useNow, useTimetable } from './timetable'

export default function Today() {
  const { me, tt, sessions, error, offline, retry } = useTimetable()
  const now = useNow()
  const [menu, setMenu] = useState(false)

  if (me && !me.setupDone) return <Navigate to="/setup" replace />
  if (!error && (!me || !tt)) return <TodaySkeleton />

  const nowMin = nowMinutes(now)
  const today = sessions.filter((s) => onDate(s, now))
  const live = today.find((s) => s.start <= nowMin && nowMin < s.end)
  const upcoming = today.filter((s) => s.start > nowMin)
  const next = live ? undefined : upcoming[0]
  const rest = upcoming.filter((s) => s !== next)

  // The next day with classes, for when today is over or free.
  const nextDate = Array.from({ length: 21 }, (_, i) => addDays(now, i + 1)).find((d) => sessions.some((s) => onDate(s, d)))
  const nextDay = nextDate ? sessions.filter((s) => onDate(s, nextDate)) : []
  const nextWeek = nextDate ? Math.round((weekStart(nextDate).getTime() - weekStart(now).getTime()) / (7 * 86_400_000)) : 0
  const nextDayLink = nextDate && `/schedule?view=day&day=${nextDate.getDay()}${nextWeek ? `&w=${nextWeek}` : ''}`
  const tomorrow = nextDate && addDays(now, 1).toDateString() === nextDate.toDateString()

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        {offline && <OfflineBanner savedAt={offline} retry={retry} />}
        <header className="flex items-start justify-between gap-4 px-6 pt-2">
          <div className="flex min-w-0 flex-col gap-0.5">
            <p className="label text-[12px] tracking-[0.14em]">
              {DAY_SHORT[now.getDay()]} {fmtDate(now)}
            </p>
            <h1 className="display text-[76px] leading-[0.86] tracking-[-0.05em]">Today.</h1>
          </div>
          <button
            type="button"
            onClick={() => setMenu(true)}
            aria-label="Open menu"
            aria-expanded={menu}
            className="press label mt-1 flex h-12 shrink-0 items-center rounded-full border-[2.5px] border-navy px-[18px] text-[12px] tint"
          >
            Menu
          </button>
        </header>

        {error ? (
          <div className="px-6 pt-6">
            <ErrorText>{error}</ErrorText>
          </div>
        ) : (
          tt && (
            <>
              <div className="px-3 pt-[22px]">
                <NeedsSection tt={tt} />
              </div>
              {sessions.length === 0 ? (
                <NoClasses semester={tt.semester?.name} />
              ) : live ? (
                <>
                  <LiveCard s={live} nowMin={nowMin} />
                  {rest.length > 0 ? <List title="Up next" sessions={rest} week /> : <LastOne>Last class today</LastOne>}
                </>
              ) : next ? (
                <>
                  <NextCard s={next} nowMin={nowMin} />
                  {rest.length > 0 ? <List title="Later today" sessions={rest} week /> : <LastOne>Nothing after this today</LastOne>}
                </>
              ) : today.length > 0 ? (
                <>
                  <Card tone="navy" title="That’s it." size={72}>
                    Your last class ended at {today[today.length - 1].meeting.end}. Nothing else on today.
                  </Card>
                  {nextDate && (
                    <List title={`${tomorrow ? 'Tomorrow' : DAY_LONG[nextDate.getDay()]} starts with`} sessions={nextDay.slice(0, 1)}>
                      <MoreLink to={nextDayLink!}>See all of {DAY_LONG[nextDate.getDay()]}</MoreLink>
                    </List>
                  )}
                </>
              ) : (
                <>
                  <Card tone="sky" title="Free day." size={80}>
                    No classes on {DAY_LONG[now.getDay()]}. Sleep in, catch up, or don’t.
                  </Card>
                  {nextDate && (
                    <List title={`Back on ${DAY_LONG[nextDate.getDay()]}`} sessions={nextDay.slice(0, 1)}>
                      <MoreLink to="/schedule">See the week</MoreLink>
                    </List>
                  )}
                </>
              )}
            </>
          )
        )}
      </div>
      <MenuSheet open={menu} onClose={() => setMenu(false)} me={me} />
    </div>
  )
}

function Tags({ s, dark }: { s: Session; dark?: boolean }) {
  return (
    <>
      {s.cls.changed && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', dark ? 'bg-cream text-navy' : 'bg-navy text-cream')}>Changed</span>}
      {s.clash && <span className="label rounded-full bg-alert-ink px-2 py-0.5 text-[9px] text-cream">Clash</span>}
    </>
  )
}

/** A class on right now: start time, progress and minutes left. */
function LiveCard({ s, nowMin }: { s: Session; nowMin: number }) {
  const total = s.end - s.start
  const done = Math.min(total, Math.max(0, nowMin - s.start))
  const left = total - done
  return (
    <Link to={`/course/${s.cls.courseId}`} className="press mx-3 flex flex-col gap-3.5 rounded-[32px] bg-navy p-[22px] pb-6 text-cream">
      <span className="flex flex-wrap items-center gap-2">
        <span className="label text-[12px] tracking-[0.14em] text-ice">● Now · {s.cls.code}</span>
        <Tags s={s} dark />
      </span>
      <span className="display text-[96px] leading-[0.8] tracking-[-0.06em] text-sky tabular-nums">{s.meeting.start}</span>
      <span className="flex flex-col gap-1">
        <span className="display text-[30px] leading-none tracking-[-0.02em] break-words hyphens-auto">{s.cls.name}</span>
        <span className="text-[15px] text-ice">
          {TYPE_NAME[s.type]}
          {s.meeting.room && ` in ${s.meeting.room}`}
          {s.cls.section.instructor && ` · ${s.cls.section.instructor}`}
        </span>
      </span>
      <span className="flex flex-col gap-2">
        <span
          className="h-3.5 overflow-hidden rounded-full bg-navy-700"
          role="progressbar"
          aria-label="Class progress"
          aria-valuemin={0}
          aria-valuemax={total}
          aria-valuenow={done}
          aria-valuetext={`${left} minutes left`}
        >
          <span className="block h-full rounded-full bg-sky transition-[width] duration-300 ease-in-out" style={{ width: `${(done / total) * 100}%` }} />
        </span>
        <span className="flex justify-between font-mono text-[12px] text-ice">
          <span>{s.meeting.start}</span>
          <span className="font-bold text-cream uppercase">{left} min left</span>
          <span>{s.meeting.end}</span>
        </span>
      </span>
    </Link>
  )
}

/** Between classes: a countdown to the next one. */
function NextCard({ s, nowMin }: { s: Session; nowMin: number }) {
  const until = s.start - nowMin
  const hint =
    until <= 20
      ? 'Head over now, it’s a short walk.'
      : until <= 60
        ? 'Time for a coffee first.'
        : s.start >= 12 * 60 && s.start <= 15 * 60
          ? 'Time for lunch first.'
          : `Plenty of time. It starts at ${s.meeting.start}.`
  return (
    <Link to={`/course/${s.cls.courseId}`} className="press mx-3 flex flex-col gap-3.5 rounded-[32px] bg-navy p-[22px] text-cream">
      <span className="flex flex-wrap items-center gap-2">
        <span className="label text-[12px] tracking-[0.14em] text-ice">Next in</span>
        <Tags s={s} dark />
      </span>
      <span aria-live="polite" className="display text-[96px] leading-[0.8] tracking-[-0.06em] text-sky tabular-nums">
        {/* Re-keyed so the new number slides in each time it changes. */}
        <span key={until} className="tick">
          {until < 60 ? until : fmtDuration(until)}
        </span>
        {until < 60 && ' min'}
      </span>
      <span className="flex flex-col gap-1">
        <span className="display text-[30px] leading-none tracking-[-0.02em] break-words hyphens-auto">{s.cls.name}</span>
        <span className="text-[15px] text-ice">
          {TYPE_NAME[s.type]}
          {s.meeting.room ? ` in ${s.meeting.room}` : ''}, starts {s.meeting.start}
        </span>
      </span>
      <span className="border-t-[1.5px] border-navy-line pt-3 text-[14px] font-semibold text-ice">{hint}</span>
    </Link>
  )
}

function Card({ tone, title, size, children }: { tone: 'navy' | 'sky'; title: string; size: number; children: ReactNode }) {
  return (
    <section className={cx('rise mx-3 flex flex-col gap-3 rounded-[32px] px-[22px] py-[26px]', tone === 'navy' ? 'bg-navy text-cream' : 'bg-sky text-navy')}>
      <h2 className={cx('display leading-[0.86] tracking-[-0.05em]', tone === 'navy' && 'text-sky')} style={{ fontSize: size }}>
        {title}
      </h2>
      <p className={cx('text-[17px] leading-[1.4] font-semibold', tone === 'navy' && 'text-ice')}>{children}</p>
    </section>
  )
}

function NoClasses({ semester }: { semester?: string }) {
  return (
    <>
      <section className="mx-3 flex flex-col gap-3.5 rounded-[32px] px-[22px] py-[26px] shadow-[inset_0_0_0_2.5px_rgb(11_25_87/0.18)]">
        <h2 className="display text-[64px] leading-[0.86] tracking-[-0.05em]">No classes yet.</h2>
        <p className="text-[17px] leading-[1.4] font-semibold">
          You haven’t picked any courses{semester ? ` for ${semester}` : ''}. It takes about a minute.
        </p>
      </section>
      <div className="mt-auto px-6 pt-8">
        <Link to="/edit" className="press flex h-[60px] items-center justify-center rounded-full bg-navy text-[18px] font-extrabold text-cream">
          Pick my courses
        </Link>
      </div>
    </>
  )
}

function LastOne({ children }: { children: ReactNode }) {
  return (
    <div className="flex items-center justify-between px-6 pt-6">
      <p className="label text-[12px] text-muted">{children}</p>
      <Link to="/schedule" className="label py-3 text-[12px] link-hover">
        Full week →
      </Link>
    </div>
  )
}

function MoreLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link to={to} className="label flex min-h-11 items-center gap-1.5 border-t-[2.5px] border-navy py-3.5 text-[12px] link-hover">
      {children} <span aria-hidden="true">→</span>
    </Link>
  )
}

function List({ title, sessions, week, children }: { title: string; sessions: Session[]; week?: boolean; children?: ReactNode }) {
  return (
    <section className="flex flex-col px-6 pt-[22px]">
      <div className="mb-2 flex items-baseline justify-between">
        <h2 className="display text-[22px] tracking-[-0.02em]">{title}</h2>
        {week && (
          <Link to="/schedule" className="label py-3 text-[12px] link-hover">
            Full week →
          </Link>
        )}
      </div>
      <ul>
        {sessions.map((s, i) => (
          <li key={i}>
            <Link to={`/course/${s.cls.courseId}`} className="grid grid-cols-[84px_minmax(0,1fr)_24px] items-center gap-3 border-t-[2.5px] border-navy py-3.5">
              <span className="display text-[34px] leading-none tracking-[-0.05em] tabular-nums">{s.meeting.start}</span>
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="flex flex-wrap items-center gap-1.5">
                  <span className="text-[18px] leading-[1.1] font-extrabold">{s.cls.name}</span>
                  <Tags s={s} />
                </span>
                <span className="truncate font-mono text-[12px] uppercase">
                  {TYPE_NAME[s.type]}, {s.meeting.room || 'Room TBA'}
                </span>
              </span>
              <Icon name="out" className="size-6" />
            </Link>
          </li>
        ))}
      </ul>
      {children}
    </section>
  )
}

/** Error / offline: the saved day stays visible below. */
function OfflineBanner({ savedAt, retry }: { savedAt: Date; retry: () => Promise<boolean> }) {
  const [trying, setTrying] = useState(false)
  const [tries, setTries] = useState(0)
  return (
    <div role="status" className="mx-3 mb-1 flex items-center gap-2.5 rounded-[24px] bg-navy py-3.5 pr-3 pl-[18px] text-cream">
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span className="text-[16px] font-extrabold">{tries ? 'Still offline' : 'You’re offline'}</span>
        <span className="text-[13px] font-semibold text-ice">Showing what we saved at {clock(savedAt.getHours() * 60 + savedAt.getMinutes())}. It may be out of date.</span>
      </span>
      <Button
        variant="sky"
        size="sm"
        className="h-11 min-w-[88px] text-[15px]"
        aria-disabled={trying}
        onClick={async () => {
          if (trying) return
          setTrying(true)
          const ok = await retry()
          setTrying(false)
          if (!ok) setTries((t) => t + 1)
        }}
      >
        <span key={String(trying)} className="msg">
          {trying ? 'Trying…' : 'Retry'}
        </span>
      </Button>
    </div>
  )
}

/** Loading: grey shapes in the exact layout of Today, with a shimmer. */
export function TodaySkeleton() {
  return (
    <div aria-busy="true" aria-label="Loading your day" className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto max-w-[480px] pt-[max(1.25rem,env(safe-area-inset-top))]">
        <header className="flex items-start justify-between px-6 pt-2">
          <div className="flex flex-col gap-2">
            <Sk className="h-3.5 w-[110px] rounded-md" />
            <span className="display text-[76px] leading-[0.86] tracking-[-0.05em]">Today.</span>
          </div>
          <Sk className="mt-1 h-12 w-[86px] rounded-full" />
        </header>
        <div className="mx-3 mt-[22px] flex flex-col gap-4 rounded-[32px] bg-navy p-[22px]">
          <Sk dark className="h-[76px] w-[220px] max-w-full rounded-2xl" />
          <Sk dark className="h-[26px] w-[240px] max-w-full rounded-lg" />
          <Sk dark className="h-4 w-40 rounded-md" />
          <Sk dark className="h-3.5 w-full rounded-full" />
        </div>
        <div className="flex flex-col gap-[18px] px-6 pt-[26px]">
          <Sk className="h-5 w-[120px] rounded-md" />
          {[70, 60].map((w) => (
            <div key={w} className="flex items-center gap-3">
              <Sk className="h-[30px] w-[84px] rounded-lg" />
              <span className="flex flex-1 flex-col gap-1.5">
                <Sk className="h-4 rounded-md" style={{ width: `${w}%` }} />
                <Sk className="h-3 rounded-md" style={{ width: `${w - 30}%` }} />
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
