import { useContext, useLayoutEffect, useRef, useState, type MouseEvent, type ReactNode } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT } from '../lib/time'
import { TYPE_NAME, addDays, changeLine, changeTag, clashOn, clock, fmtDate, fmtDuration, isoDate, nowMinutes, onDate, sessionCode, sessionHref, type Session } from '../lib/slots'
import { Button, ErrorText, Sk, cx } from '../components/ui'
import { useHome, useViewer } from '../lib/viewer'
import TeachBanner from '../teacher/TeachBanner'
import { MenuSheet } from './StudentShell'
import { Icon, NeedsSection, useNow } from './timetable'
import { LabNav } from './labNav'
import RatePrompt from './RatePrompt'

/**
 * A class's link: inside the home page, a class opens its session page and one of your own slots opens to edit;
 * elsewhere, the course page (or the day, for your own slot).
 */
function useClassLink() {
  const lab = useContext(LabNav)
  return (s: Session, date?: Date): { to: string; onClick?: (e: MouseEvent) => void } => {
    if (!lab) return { to: sessionHref(s) }
    // A teacher's office hours have no session page: they open where they are edited.
    if (s.office) return { to: '/teach/office' }
    const mine = s.mine
    if (!mine) return { to: lab.session(s, date) }
    return {
      to: sessionHref(s),
      onClick: (e) => {
        e.preventDefault()
        lab.edit(mine)
      },
    }
  }
}

/** The same day actions remain available with upcoming classes, after classes, and on free days. */
function DayActions({ date }: { date: Date }) {
  const lab = useContext(LabNav)
  const viewer = useViewer()
  const { teach } = useHome()
  // A student adds their own slot. A teacher chooses between that and an extra class; an owner viewing as a teacher
  // has no own slots, so theirs is the extra class.
  const label = viewer.mode === 'student' ? '+ Add a slot' : teach?.personal ? '+ Add a slot or class' : '+ Add an extra class'
  return (
    <div className="flex items-center justify-between gap-4 px-6 pt-4">
      {lab && (viewer.mode === 'student' || teach) && (
        <button type="button" onClick={() => lab.add(date)} className="label flex min-h-11 items-center text-[12px] link-hover">
          {label}
        </button>
      )}
      <WeekLink />
    </div>
  )
}

export default function Today() {
  const viewer = useViewer()
  const student = viewer.mode === 'student'
  const { me, teach, tt, sessions, ghosts, error, offline, retry } = useHome()
  const now = useNow()
  const [menu, setMenu] = useState(false)
  const lab = useContext(LabNav)

  if (student && me && !me.setupDone) return <Navigate to="/setup" replace />
  // A teacher whose email matched no name picks theirs first. Never a teaching assistant or an owner looking in:
  // the name step isn't theirs, and would send them straight back.
  if (!student && teach?.kind === 'staff' && teach.status === 'pick-name') return <Navigate to="/teach/name" replace />
  if (!error && (!(student ? me : teach) || !tt)) return <TodaySkeleton />

  const nowMin = nowMinutes(now)
  const today = sessions.filter((s) => onDate(s, now))
  // Clashing classes can be on at the same time: the card shows the first, the list the others.
  const liveAll = today.filter((s) => s.start <= nowMin && nowMin < s.end)
  const live = liveAll[0]
  const upcoming = today.filter((s) => s.start > nowMin)
  const next = live ? undefined : upcoming[0]
  const rest = [...liveAll.slice(1), ...upcoming.filter((s) => s !== next)]
  // Cancelled classes and the old slots of moved ones: listed greyed, never counted above.
  const off = ghosts.filter((s) => onDate(s, now))

  // The next day with classes, for when today is over or free.
  const nextDate = Array.from({ length: 21 }, (_, i) => addDays(now, i + 1)).find((d) => sessions.some((s) => onDate(s, d)))
  const nextDay = nextDate ? sessions.filter((s) => onDate(s, nextDate)) : []
  const tomorrow = nextDate && addDays(now, 1).toDateString() === nextDate.toDateString()

  return (
    <div className="min-h-[calc(100dvh-var(--week-cue,0px))] bg-cream text-navy">
      <div className="mx-auto flex min-h-[calc(100dvh-var(--week-cue,0px))] max-w-[480px] flex-col pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        {offline && <OfflineBanner savedAt={offline} retry={retry} />}
        {student && !offline && me && <RatePrompt />}
        <header className="flex items-start justify-between gap-4 px-6 pt-2">
          <div className="flex min-w-0 flex-col gap-0.5">
            <p className="label text-[12px] tracking-[0.14em]">
              {DAY_SHORT[now.getDay()]} {fmtDate(now)}
            </p>
            <h1 className="display text-[76px] leading-[0.86] tracking-[-0.05em]">Today.</h1>
          </div>
          <button
            type="button"
            onClick={() => (lab ? lab.menu() : setMenu(true))}
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
              <div className="px-3 pt-[22px]">{student ? <NeedsSection tt={tt} /> : <TeachBanner />}</div>
              {sessions.length === 0 ? (
                <NoClasses semester={tt.semester?.name} teacher={!student} />
              ) : live ? (
                <>
                  <LiveCard s={live} nowMin={nowMin} date={now} />
                  {rest.length + off.length > 0 ? <List title={rest.length > 0 ? 'Up next' : 'Not on today'} sessions={rest} ghosts={off} date={now} /> : <LastOne>Last class today</LastOne>}
                </>
              ) : next ? (
                <>
                  <NextCard s={next} nowMin={nowMin} date={now} />
                  {rest.length + off.length > 0 ? <List title={rest.length > 0 ? 'Later today' : 'Not on today'} sessions={rest} ghosts={off} date={now} /> : <LastOne>Nothing after this today</LastOne>}
                </>
              ) : today.length > 0 ? (
                <>
                  <Card tone="navy" title="That’s it." size={72}>
                    Your last class ended at {today[today.length - 1].meeting.end}. Nothing else on today.
                  </Card>
                  {off.length > 0 && <List title="Not on today" sessions={[]} ghosts={off} date={now} />}
                  {nextDate && (
                    <List title={tomorrow ? 'Tomorrow' : DAY_LONG[nextDate.getDay()]} sessions={nextDay} date={nextDate}>
                      <MoreLink to="/" onLab={() => lab?.day(nextDate)}>
                        See all of {DAY_LONG[nextDate.getDay()]}
                      </MoreLink>
                    </List>
                  )}
                </>
              ) : (
                <>
                  {off.length > 0 ? <NothingOn ghosts={off} date={now} /> : <FreeDay date={now} dayOff={tt.daysOff?.find((d) => d.date === isoDate(now))?.name} />}
                  {nextDate && (
                    <List title={`Back on ${DAY_LONG[nextDate.getDay()]}`} sessions={nextDay} date={nextDate} />
                  )}
                </>
              )}
            </>
          )
        )}
        {tt && <DayActions date={now} />}
      </div>
      {!lab && student && <MenuSheet open={menu} onClose={() => setMenu(false)} me={me} />}
    </div>
  )
}

/**
 * Any other day in the home screen's design: swiping sideways on Today lands here. The day's first class is the big
 * card and the rest are listed under it, the way Today shows what's next.
 */
export function DayHome({ date, offset, sessions, ghosts, dayOff }: { date: Date; offset: number; sessions: Session[]; ghosts: Session[]; dayOff?: string }) {
  const lab = useContext(LabNav)
  const list = sessions.filter((s) => onDate(s, date))
  const [first, ...rest] = list
  const off = ghosts.filter((s) => onDate(s, date))
  const title = offset === 1 ? 'Tomorrow.' : offset === -1 ? 'Yesterday.' : `${DAY_LONG[date.getDay()]}.`
  return (
    <div className="min-h-[calc(100dvh-var(--week-cue,0px))] bg-cream text-navy">
      <div className="mx-auto flex min-h-[calc(100dvh-var(--week-cue,0px))] max-w-[480px] flex-col pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        <header className="flex items-start justify-between gap-4 px-6 pt-2">
          <div className="flex min-w-0 flex-1 flex-col gap-0.5">
            <p className="label text-[12px] tracking-[0.14em]">
              {DAY_SHORT[date.getDay()]} {fmtDate(date)}
            </p>
            <FitTitle>{title}</FitTitle>
          </div>
          <button
            type="button"
            onClick={() => lab?.menu()}
            aria-label="Open menu"
            className="press label mt-1 flex h-12 shrink-0 items-center rounded-full border-[2.5px] border-navy px-[18px] text-[12px] tint"
          >
            Menu
          </button>
        </header>
        <div className="pt-[22px]" />
        {first ? (
          <>
            <DayCard s={first} count={list.length} end={list[list.length - 1].meeting.end} date={date} />
            {rest.length + off.length > 0 ? <List title={rest.length > 0 ? 'Later' : 'Not on'} sessions={rest} ghosts={off} date={date} /> : <LastOne>Only class that day</LastOne>}
          </>
        ) : off.length > 0 ? (
          <NothingOn ghosts={off} date={date} />
        ) : (
          <FreeDay date={date} dayOff={dayOff} />
        )}
        <DayActions date={date} />
      </div>
    </div>
  )
}

/** A day with nothing on: a holiday by name when it's a day off, otherwise just free. */
function FreeDay({ date, dayOff }: { date: Date; dayOff?: string }) {
  return dayOff ? (
    <Card tone="navy" title="Day off." size={80}>
      {dayOff}: no classes on {DAY_LONG[date.getDay()]}. Enjoy it.
    </Card>
  ) : (
    <Card tone="sky" title="Free day." size={80}>
      No classes on {DAY_LONG[date.getDay()]}. Sleep in, catch up, or don’t.
    </Card>
  )
}

/** A day whose classes were all cancelled or moved away: it says so, over the greyed rows. */
function NothingOn({ ghosts, date }: { ghosts: Session[]; date: Date }) {
  const cancelled = ghosts.filter((s) => s.off === 'cancelled').length
  const moved = ghosts.length - cancelled
  const count = (n: number, what: string) => `${n} ${n === 1 ? 'class' : 'classes'} ${what}`
  return (
    <>
      <Card tone="sky" title="Nothing on." size={72}>
        {[cancelled > 0 && count(cancelled, 'cancelled'), moved > 0 && count(moved, 'moved')].filter(Boolean).join(', ')}.
      </Card>
      <List title={`Not on ${DAY_LONG[date.getDay()]}`} sessions={[]} ghosts={ghosts} date={date} />
    </>
  )
}

/** The day's headline at Today's size, stepped down only as far as it needs to fit beside the Menu button. */
export function FitTitle({ children }: { children: string }) {
  const ref = useRef<HTMLHeadingElement>(null)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    let size = 76
    el.style.fontSize = `${size}px`
    while (el.scrollWidth > el.clientWidth && size > 40) {
      size -= 2
      el.style.fontSize = `${size}px`
    }
  }, [children])
  return (
    <h1 ref={ref} className="display overflow-hidden leading-[0.86] tracking-[-0.05em] whitespace-nowrap">
      {children}
    </h1>
  )
}

/** Another day's first class, in the same card as Today's "Next in". */
function DayCard({ s, count, end, date }: { s: Session; count: number; end: string; date: Date }) {
  const link = useClassLink()
  return (
    <Link {...link(s, date)} className="press mx-3 flex flex-col gap-3.5 rounded-[32px] bg-navy p-[22px] text-cream">
      <span className="flex flex-wrap items-center gap-2">
        <span className="label text-[12px] tracking-[0.14em] text-ice">First class · {sessionCode(s, date)}</span>
        <Tags s={s} date={date} dark />
      </span>
      <span className="display text-[96px] leading-[0.8] tracking-[-0.06em] text-sky tabular-nums">{s.meeting.start}</span>
      <span className="flex flex-col gap-1">
        <span className="display text-[30px] leading-none tracking-[-0.02em] break-words hyphens-auto">{s.cls.name}</span>
        <span className="text-[15px] text-ice">
          {TYPE_NAME[s.type]}
          {s.meeting.room ? ` in ${s.meeting.room}` : ''}, ends {s.meeting.end}
        </span>
      </span>
      <span className="border-t-[1.5px] border-navy-line pt-3 text-[14px] font-semibold text-ice">
        {count === 1 ? `One class, done by ${end}.` : `${count} classes, done by ${end}.`}
      </span>
    </Link>
  )
}

function Tags({ s, date, dark }: { s: Session; date: Date; dark?: boolean }) {
  return (
    <>
      {s.cls.changed && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', dark ? 'bg-cream text-navy' : 'bg-navy text-cream')}>Changed</span>}
      {/* A teacher's change to this one session: moved here, another room, or an extra class. */}
      {changeTag(s) && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', dark ? 'bg-sky text-navy' : 'bg-ice text-navy shadow-[inset_0_0_0_1.5px_var(--color-navy)]')}>{changeTag(s)}</span>}
      {clashOn(s, date) && <span className="label rounded-full bg-alert-ink px-2 py-0.5 text-[9px] text-cream">Clash</span>}
    </>
  )
}

/** A class on right now: start time, progress and minutes left. */
function LiveCard({ s, nowMin, date }: { s: Session; nowMin: number; date: Date }) {
  const link = useClassLink()
  const total = s.end - s.start
  const done = Math.min(total, Math.max(0, nowMin - s.start))
  const left = total - done
  return (
    <Link {...link(s)} className="press mx-3 flex flex-col gap-3.5 rounded-[32px] bg-navy p-[22px] pb-6 text-cream">
      <span className="flex flex-wrap items-center gap-2">
        <span className="label text-[12px] tracking-[0.14em] text-ice">● Now · {sessionCode(s, date)}</span>
        <Tags s={s} date={date} dark />
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
function NextCard({ s, nowMin, date }: { s: Session; nowMin: number; date: Date }) {
  const link = useClassLink()
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
    <Link {...link(s)} className="press mx-3 flex flex-col gap-3.5 rounded-[32px] bg-navy p-[22px] text-cream">
      <span className="flex flex-wrap items-center gap-2">
        <span className="label text-[12px] tracking-[0.14em] text-ice">Next in</span>
        <Tags s={s} date={date} dark />
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

function NoClasses({ semester, teacher }: { semester?: string; teacher?: boolean }) {
  return (
    <>
      <section className="mx-3 flex flex-col gap-3.5 rounded-[32px] px-[22px] py-[26px] shadow-[inset_0_0_0_2.5px_rgb(11_25_87/0.18)]">
        <h2 className="display text-[64px] leading-[0.86] tracking-[-0.05em]">No classes yet.</h2>
        <p className="text-[17px] leading-[1.4] font-semibold">
          {teacher ? 'Add the classes you teach' : 'You haven’t picked any courses'}
          {semester ? ` for ${semester}` : ''}. It takes about a minute.
        </p>
      </section>
      <div className="mt-auto px-6 pt-8">
        <Link to={teacher ? '/teach/classes' : '/edit'} className="press flex h-[60px] items-center justify-center rounded-full bg-navy text-[18px] font-extrabold text-cream">
          {teacher ? 'Add my classes' : 'Pick my courses'}
        </Link>
      </div>
    </>
  )
}

function LastOne({ children }: { children: ReactNode }) {
  return (
    <div className="px-6 pt-6">
      <p className="label text-[12px] text-muted">{children}</p>
    </div>
  )
}

/** "Full week →" on screens away from home. On the home page the cue at the foot of the day leads down to the week. */
function WeekLink() {
  const lab = useContext(LabNav)
  if (lab) return null
  return (
    <Link to="/" className="label ml-auto flex min-h-11 items-center py-3 text-[12px] link-hover">
      Full week →
    </Link>
  )
}

function MoreLink({ to, onLab, children }: { to: string; onLab: () => void; children: ReactNode }) {
  const lab = useContext(LabNav)
  const cls = 'label flex min-h-11 w-full items-center gap-1.5 border-t-[2.5px] border-navy py-3.5 text-[12px] link-hover'
  return lab ? (
    <button type="button" onClick={onLab} className={cls}>
      {children} <span aria-hidden="true">→</span>
    </button>
  ) : (
    <Link to={to} className={cls}>
      {children} <span aria-hidden="true">→</span>
    </Link>
  )
}

function List({ title, sessions, ghosts = [], date, children }: { title: string; sessions: Session[]; ghosts?: Session[]; date: Date; children?: ReactNode }) {
  const link = useClassLink()
  // A teacher can have two sections of one course in a day, and one class under several codes: the row says which.
  const teacher = useViewer().mode === 'teacher'
  // Cancelled and moved-away classes keep their place in the day, by time.
  const rows = [...sessions, ...ghosts].sort((a, b) => a.start - b.start)
  return (
    <section className="flex flex-col px-6 pt-[22px]">
      <div className="mb-2 flex items-baseline justify-between">
        <h2 className="display text-[22px] tracking-[-0.02em]">{title}</h2>
      </div>
      <ul>
        {rows.map((s, i) => (
          <li key={i}>
            <Link {...link(s, date)} className="grid grid-cols-[84px_minmax(0,1fr)_24px] items-center gap-3 border-t-[2.5px] border-navy py-3.5">
              <span className={cx('display text-[34px] leading-none tracking-[-0.05em] tabular-nums', s.off && 'text-navy/45 line-through decoration-[3px]')}>{s.meeting.start}</span>
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="flex flex-wrap items-center gap-1.5">
                  <span className={cx('text-[18px] leading-[1.1] font-extrabold', s.off && 'text-navy/55')}>{s.cls.name}</span>
                  {!s.off && <Tags s={s} date={date} />}
                </span>
                {s.off ? (
                  <span className="font-mono text-[12px] font-bold uppercase">
                    {teacher && `${sessionCode(s, date)} · `}
                    {changeLine(s)}
                  </span>
                ) : (
                  <span className="truncate font-mono text-[12px] uppercase">
                    {TYPE_NAME[s.type]}, {s.meeting.room || 'Room TBA'}
                    {teacher && !s.mine && !s.office && ` · ${sessionCode(s, date)}${!s.merged && s.cls.section.number ? ` sec ${s.cls.section.number}` : ''}`}
                  </span>
                )}
              </span>
              <Icon name="out" className={cx('size-6', s.off && 'text-navy/45')} />
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
