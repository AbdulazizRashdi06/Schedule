import { useState } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT } from '../lib/time'
import { TYPE_NAME, addDays, fmtDate, fmtDuration, nowMinutes, type Session } from '../lib/slots'
import { ErrorText, Loading, cx } from '../components/ui'
import { MenuSheet } from './StudentShell'
import { Icon, NeedsSection, RoundButton, useNow, useTimetable } from './timetable'

export default function Today() {
  const { me, tt, sessions, error } = useTimetable()
  const now = useNow()
  const [menu, setMenu] = useState(false)

  if (me && !me.setupDone) return <Navigate to="/setup" replace />

  const nowMin = nowMinutes(now)
  const today = sessions.filter((s) => s.meeting.day === now.getDay())
  const live = today.find((s) => s.start <= nowMin && nowMin < s.end)
  const upcoming = today.filter((s) => s.start > nowMin)
  const hero = live ?? upcoming[0]
  const rest = upcoming.filter((s) => s !== hero)

  // The next day with classes, for when today is over or free.
  const nextDate = [1, 2, 3, 4, 5, 6, 7].map((i) => addDays(now, i)).find((d) => sessions.some((s) => s.meeting.day === d.getDay()))
  const nextDay = nextDate ? sessions.filter((s) => s.meeting.day === nextDate.getDay()) : []

  return (
    <div className="min-h-dvh bg-cream font-display text-navy">
      <div className="mx-auto max-w-md px-6 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(2rem,env(safe-area-inset-bottom))]">
        <header className="mb-6 flex items-start justify-between gap-4">
          <div className="min-w-0">
            <p className="label text-[11px] text-navy/60">
              {DAY_SHORT[now.getDay()]} · {fmtDate(now)}
              {tt?.semester && ` · ${tt.semester.name}`}
            </p>
            <h1 className="display mt-1 text-[76px] leading-[0.9] tracking-[-0.06em]">Today.</h1>
          </div>
          <RoundButton label="Menu" onClick={() => setMenu(true)} className="mt-1 border-2 border-navy/80 text-navy hover:bg-navy/5">
            <Icon name="menu" />
          </RoundButton>
        </header>

        {error ? (
          <ErrorText>{error}</ErrorText>
        ) : !me || !tt ? (
          <Loading />
        ) : (
          <>
            <NeedsSection tt={tt} />
            {sessions.length === 0 ? (
              <EmptyCard />
            ) : hero ? (
              <HeroCard session={hero} live={hero === live} nowMin={nowMin} />
            ) : (
              <DoneCard hadClasses={today.length > 0} next={nextDay[0]} nextDate={nextDate} />
            )}

            {rest.length > 0 ? (
              <List title="Up next" sessions={rest} />
            ) : (
              !hero && nextDate && nextDay.length > 0 && <List title={`${DAY_LONG[nextDate.getDay()]} · ${fmtDate(nextDate)}`} sessions={nextDay} />
            )}
            {sessions.length > 0 && rest.length === 0 && hero && (
              <div className="mt-8 flex items-center justify-between">
                <p className="label text-[11px] text-navy/60">{live ? 'Last class today' : 'Nothing after this today'}</p>
                <FullWeek />
              </div>
            )}
          </>
        )}
      </div>
      <MenuSheet open={menu} onClose={() => setMenu(false)} setupDone={me?.setupDone} theme />
    </div>
  )
}

function FullWeek() {
  return (
    <Link to="/schedule" className="label -my-3 flex min-h-11 items-center gap-1.5 text-[11px] text-navy">
      Full week <Icon name="arrow" className="size-3.5" />
    </Link>
  )
}

function Tags({ s, dark }: { s: Session; dark?: boolean }) {
  return (
    <>
      {s.cls.changed && <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', dark ? 'bg-cream text-navy' : 'bg-navy text-cream')}>Changed</span>}
      {s.clash && <span className="label rounded-full bg-red-600 px-2 py-0.5 text-[9px] text-white">Clash</span>}
    </>
  )
}

function HeroCard({ session: s, live, nowMin }: { session: Session; live: boolean; nowMin: number }) {
  const total = s.end - s.start
  const done = Math.min(total, Math.max(0, nowMin - s.start))
  const left = total - done
  const until = s.start - nowMin
  return (
    <Link to={`/course/${s.cls.courseId}`} className="block rounded-[32px] bg-navy p-6 text-cream">
      <div className="flex flex-wrap items-center gap-2">
        <span className="label text-[11px] text-ice">
          {live ? '● Now' : `Next in ${fmtDuration(until)}`} · {s.cls.code}
        </span>
        <Tags s={s} dark />
      </div>
      <div className="display mt-2 text-[96px] leading-[0.85] tracking-[-0.06em] text-sky tabular-nums">{s.meeting.start}</div>
      <h2 className="display mt-4 text-[26px] leading-[1.05] tracking-[-0.03em] break-words hyphens-auto">{s.cls.name}</h2>
      <p className="mt-1.5 text-[15px] font-semibold text-ice">
        {TYPE_NAME[s.type]}
        {s.cls.section.instructor && ` · ${s.cls.section.instructor}`}
      </p>

      {live ? (
        <div className="mt-6">
          <div
            className="h-2 overflow-hidden rounded-full bg-navy-700"
            role="progressbar"
            aria-label="Class progress"
            aria-valuemin={0}
            aria-valuemax={total}
            aria-valuenow={done}
            aria-valuetext={`${left} minutes left`}
          >
            <div className="h-full rounded-full bg-sky" style={{ width: `${(done / total) * 100}%` }} />
          </div>
          <div className="label mt-2.5 flex justify-between text-[10px] text-ice">
            <span>{s.meeting.start}</span>
            <span className="text-sky">{left} min left</span>
            <span>{s.meeting.end}</span>
          </div>
        </div>
      ) : (
        <div className="label mt-6 flex justify-between border-t border-navy-line pt-3 text-[10px] text-ice">
          <span>{s.meeting.room || 'Room TBA'}</span>
          <span>
            {s.meeting.start} – {s.meeting.end}
          </span>
        </div>
      )}
    </Link>
  )
}

function DoneCard({ hadClasses, next, nextDate }: { hadClasses: boolean; next?: Session; nextDate?: Date }) {
  return (
    <div className="rounded-[32px] bg-navy p-6 text-cream">
      <p className="label text-[11px] text-ice">{hadClasses ? 'Done for today' : 'No classes today'}</p>
      <div className="display mt-2 text-[60px] leading-[0.9] tracking-[-0.05em] text-sky">{hadClasses ? "That's it." : 'Free day.'}</div>
      {next && nextDate && (
        <p className="label mt-6 border-t border-navy-line pt-4 text-[10px] text-ice">
          Next class · {DAY_SHORT[nextDate.getDay()]} {fmtDate(nextDate)} · {next.meeting.start}
        </p>
      )}
    </div>
  )
}

function EmptyCard() {
  return (
    <div className="rounded-[32px] bg-navy p-6 text-cream">
      <p className="label text-[11px] text-ice">Nothing here yet</p>
      <div className="display mt-2 text-[52px] leading-[0.9] tracking-[-0.05em] text-sky">No classes.</div>
      <Link to="/edit" className="mt-6 flex h-14 items-center justify-center gap-2 rounded-full bg-cream text-[16px] font-semibold text-navy">
        Add your courses <Icon name="arrow" className="size-4" />
      </Link>
    </div>
  )
}

function List({ title, sessions }: { title: string; sessions: Session[] }) {
  return (
    <section className="mt-8">
      <div className="mb-1 flex items-center justify-between">
        <h2 className="label text-[11px] text-navy/60">{title}</h2>
        <FullWeek />
      </div>
      <ul>
        {sessions.map((s, i) => (
          <li key={i} className="border-b border-navy/12 last:border-0">
            <Link to={`/course/${s.cls.courseId}`} className="flex items-center gap-4 py-4">
              <span className="display w-[92px] shrink-0 text-[34px] leading-none tracking-[-0.04em] tabular-nums">{s.meeting.start}</span>
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-1.5">
                  <span className="truncate text-[16px] font-semibold">{s.cls.name}</span>
                  <Tags s={s} />
                </span>
                <span className="label mt-1 block truncate text-[10px] text-navy/60">
                  {TYPE_NAME[s.type]} · {s.meeting.room || 'Room TBA'}
                </span>
              </span>
              <Icon name="arrow" className="text-navy/70" />
            </Link>
          </li>
        ))}
      </ul>
    </section>
  )
}

