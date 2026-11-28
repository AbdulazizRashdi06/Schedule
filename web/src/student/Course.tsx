import { useEffect, useState } from 'react'
import { Navigate, useParams } from 'react-router-dom'
import { DAY_SHORT, fmtRange } from '../lib/time'
import { TYPE_NAME, addDays, changeLine, changeTag, clashOn, fmtDate, inWeek, isoDate, nowMinutes, onDate, type Session } from '../lib/slots'
import { ErrorText, Sk, cx } from '../components/ui'
import ClassNotes from './ClassNotes'
import ClassTeachers from './ClassTeachers'
import NotFound from './NotFound'
import { Icon, RoundButton, useBack, useNow, useTimetable } from './timetable'

export default function Course() {
  const { courseId } = useParams()
  const { me, tt, sessions, ghosts, error, markSeen, retry } = useTimetable()
  const now = useNow()
  const back = useBack('/')
  const [soon, setSoon] = useState(false)

  const cls = tt?.classes.find((c) => c.courseId === Number(courseId))
  const changed = cls?.changed
  const sectionId = cls?.section.id
  useEffect(() => {
    if (changed && sectionId) markSeen(sectionId)
  }, [changed, sectionId, markSeen])

  if (me && !me.setupDone) return <Navigate to="/setup" replace />

  const all = sessions.filter((s) => s.cls.courseId === Number(courseId))
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const nowMin = nowMinutes(now)
  const live = all.find((s) => onDate(s, now) && s.start <= nowMin && nowMin < s.end)
  // The next session from now.
  const next = all
    .filter((s) => s !== live)
    .map((s) => ({ s, wait: until(s, now) }))
    .filter((x) => x.wait < Infinity)
    .sort((a, b) => a.wait - b.wait)[0]?.s
  // Every weekly time, even in a week it doesn't meet (said on the row), plus dated classes (timetables that change week
  // to week) in the coming seven days. With only dated classes and none of those soon, the first seven days that have one.
  const weekly = all.filter((s) => !s.meeting.date)
  let from = today
  let dated = inWeek(all.filter((s) => s.meeting.date), from)
  if (weekly.length === 0 && dated.length === 0 && next?.meeting.date) {
    from = new Date(`${next.meeting.date}T00:00`)
    dated = inWeek(all.filter((s) => s.meeting.date), from)
  }
  // Sessions a teacher cancelled or moved away in those days stay listed, greyed: on their weekly row when there is
  // one, otherwise as a row of their own.
  const off = inWeek(ghosts.filter((g) => g.cls.courseId === Number(courseId)), from)
  const ghostOf = (s: Session, date: Date) => off.find((g) => g.meeting.date === isoDate(date) && g.meeting.start === s.meeting.start)
  const rows = [...all.filter((s) => weekly.includes(s) || dated.includes(s)), ...off]
    .map((s) => ({ s, date: s.meeting.date ? new Date(`${s.meeting.date}T12:00`) : addDays(from, (s.meeting.day - from.getDay() + 7) % 7) }))
    .filter(({ s, date }) => !s.off || !weekly.some((w) => w.meeting.day === s.meeting.day && ghostOf(w, date) === s))
    // A weekly time whose room a teacher changed this week is listed once, as the re-roomed session.
    .filter(({ s, date }) => s.meeting.date || onDate(s, date) || !dated.some((d) => d.change?.kind === 'room' && d.meeting.date === isoDate(date) && d.meeting.start === s.meeting.start))
    .sort((a, b) => a.s.meeting.day - b.s.meeting.day || a.s.start - b.s.start)
  const types = [...new Set(rows.map(({ s }) => TYPE_NAME[s.type]))].join(' & ')

  if (!error && me && tt && !cls) return <NotFound course />

  return (
    <div className="page-sky min-h-dvh bg-sky text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(1rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3 px-6">
          <RoundButton label="Back" onClick={back} className="bg-navy text-sky">
            <Icon name="back" />
          </RoundButton>
          {cls && (
            <span className="label rounded-full border-[2.5px] border-navy px-3 py-2 text-[12px] tracking-[0.14em]">
              {cls.code} · Section {cls.section.number}
            </span>
          )}
        </header>

        {error ? (
          <div className="mt-8 px-6">
            <ErrorText>{error}</ErrorText>
          </div>
        ) : !me || !tt || !cls ? (
          <div aria-busy="true" aria-label="Loading the course" className="flex flex-col gap-3 px-6 pt-[18px]">
            <Sk className="h-[46px] w-3/4 rounded-xl" />
            <Sk className="h-[46px] w-1/2 rounded-xl" />
            <Sk className="h-4 w-40 rounded-md" />
            <div className="-mx-3 mt-3 flex flex-col gap-3 rounded-[32px] bg-navy p-5">
              <Sk dark className="h-[60px] rounded-[18px]" />
              <Sk dark className="h-[60px] rounded-[18px]" />
              <Sk dark className="h-11 w-2/3 rounded-full" />
            </div>
          </div>
        ) : (
          <>
            <div className="flex flex-col gap-1.5 px-6 pt-[18px]">
              <h1 className="display text-[52px] leading-[0.88] tracking-[-0.04em] text-balance break-words hyphens-auto">{cls.name}</h1>
              <p className="text-[15px] font-semibold">
                {[
                  types || cls.type,
                  cls.credits != null && `${cls.credits} ${cls.credits === 1 ? 'credit' : 'credits'}`,
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </p>
            </div>

            {cls.section.changeSummary && cls.section.changedAt && (
              <div className="mx-3 mt-4 rounded-[18px] bg-cream px-4 py-3">
                <p className="label text-[10px]">Changed {new Date(cls.section.changedAt).toLocaleDateString(undefined, { timeZone: 'Asia/Muscat', day: 'numeric', month: 'short' })}</p>
                <p className="mt-1 text-[14px] font-semibold">{cls.section.changeSummary}</p>
              </div>
            )}

            <section className="mx-3 mt-[18px] mb-2 flex flex-1 flex-col gap-[18px] rounded-[32px] bg-navy p-5 text-cream">
              {all.some(s => s.type === 'exam' && s.meeting.date! >= isoDate(now)) && <div className="flex flex-col gap-2" aria-label="Course exams"><h2 className="text-[18px] font-extrabold">Upcoming exams</h2>{all.filter(s => s.type === 'exam' && s.meeting.date! >= isoDate(now)).sort((a, b) => a.meeting.date!.localeCompare(b.meeting.date!)).map(s => <div key={`${s.plannerEventId}-${s.meeting.date}`} className="rounded-[14px] bg-sky p-3 text-[14px] font-bold text-navy"><p>{s.cls.name}</p><p>{s.meeting.date} · {fmtRange(s.meeting.start, s.meeting.end)}</p><p>{s.meeting.room || 'Location not set'}</p>{s.plannerNotes && <p className="mt-1 font-semibold whitespace-pre-wrap">{s.plannerNotes}</p>}</div>)}</div>}
              <div className="flex flex-col gap-2">
                <h2 className="text-[18px] font-extrabold">When &amp; where</h2>
                {from !== today && <p className="text-[13px] font-semibold text-ice">Nothing in the coming week. From {fmtDate(from)}:</p>}
                {rows.length === 0 && <p className="text-[13px] font-semibold text-ice">No more classes this semester.</p>}
                <ul className="flex flex-col gap-2">
                  {rows.map(({ s, date }, i) => {
                    const isLive = s === live
                    const isNext = s === next && !live
                    // A weekly time that doesn't meet in the coming week: before week 1, after the last, or a day off.
                    const ghost = s.off ? s : !onDate(s, date) ? ghostOf(s, date) : undefined
                    const tag = changeTag(s)
                    const off = ghost ? changeTag(ghost) : !onDate(s, date) && (s.firstDate && isoDate(date) < s.firstDate ? `From ${fmtDate(firstOn(s, date) ?? date)}` : s.lastDate && isoDate(date) > s.lastDate ? 'Ended' : 'Not this week')
                    return (
                      <li
                        key={i}
                        className={cx('grid grid-cols-[56px_minmax(0,1fr)_auto] items-center gap-3 rounded-[18px] px-3.5 py-3', isLive ? 'bg-sky text-navy' : 'bg-navy-800')}
                      >
                        <span className={cx('label text-[12px] tracking-normal', !isLive && 'text-ice')}>{DAY_SHORT[s.meeting.day]}</span>
                        <span className="flex min-w-0 flex-col gap-0.5">
                          <span className={cx('text-[18px] leading-none font-extrabold tabular-nums', ghost && 'text-ice/70 line-through decoration-2')}>
                            {fmtRange(s.meeting.start, s.meeting.end)}
                          </span>
                          <span className={cx('text-[13px] font-semibold', !isLive && 'text-ice')}>
                            {s.meeting.room ? `Room ${s.meeting.room}` : 'Room TBA'} · {TYPE_NAME[s.type]}
                          </span>
                          {ghost && <span className="text-[13px] font-semibold text-ice">{changeLine(ghost)}</span>}
                        </span>
                        <span className="flex flex-col items-end gap-1 text-right">
                          {!ghost && tag && <span className="label rounded-full bg-sky px-2 py-1 text-[10px] text-navy">{tag}</span>}
                          {isLive && <span className="label rounded-full bg-navy px-2 py-1 text-[10px] text-sky">● Now</span>}
                          {isNext && <span className="label text-[10px] text-ice">Next</span>}
                          {off && <span className="label text-[10px] text-ice">{off}</span>}
                          {!off && clashOn(s, date) && <span className="label rounded-full bg-alert-ink px-2 py-1 text-[10px] text-cream">Clash</span>}
                        </span>
                      </li>
                    )
                  })}
                </ul>
              </div>

              <ClassTeachers cls={cls} />

              <ClassNotes courseId={cls.courseId} section={cls.section} me={me?.universityId ?? ''} onChanged={() => void retry()} />

              {/* Reminders aren't built yet: the button is in place and says so when tapped. */}
              <div className="mt-auto flex flex-col gap-2">
                <button
                  type="button"
                  onClick={() => setSoon(true)}
                  className="press flex h-14 w-full shrink-0 items-center justify-center gap-2.5 rounded-full bg-cream text-[17px] font-extrabold text-navy"
                >
                  <Icon name="bell" />
                  Remind me 15 min before
                </button>
                <p className="label min-h-4 text-center text-[10px] text-ice" role="status">
                  {soon && <span className="msg inline-block">Reminders are coming soon</span>}
                </p>
              </div>
            </section>
          </>
        )}
      </div>
    </div>
  )
}

/** The first date from `date` on that a weekly session meets (a year on is as far as we look). */
function firstOn(s: Session, date: Date) {
  for (let days = 0; days < 366; days += 7) if (onDate(s, addDays(date, days))) return addDays(date, days)
  return null
}

/** Minutes from now until the session next starts: round the week for weekly classes, Infinity for a dated class that's over. */
function until(s: Session, now: Date) {
  const nowMin = nowMinutes(now)
  if (s.meeting.date) {
    // Cancelled or moved by the planner: it isn't coming.
    if (!onDate(s, new Date(`${s.meeting.date}T12:00`))) return Infinity
    const [y, m, d] = s.meeting.date.split('-').map(Number)
    const days = Math.round((new Date(y, m - 1, d).getTime() - new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()) / 86_400_000)
    const wait = days * 1440 + s.start - nowMin
    return wait > 0 ? wait : Infinity
  }
  let days = (s.meeting.day - now.getDay() + 7) % 7
  if (days === 0 && s.start <= nowMin) days = 7
  // Skip the weeks it doesn't meet: days off, and weeks before or after the teaching weeks (a year on is as far as we look).
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  for (; days < 366; days += 7) if (onDate(s, addDays(today, days))) return days * 1440 + s.start - nowMin
  return Infinity
}
