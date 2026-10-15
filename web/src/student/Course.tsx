import { useEffect, useState } from 'react'
import { Navigate, useParams } from 'react-router-dom'
import { DAY_SHORT } from '../lib/time'
import { TYPE_NAME, inWeek, initials, nowMinutes, onDate, type Session } from '../lib/slots'
import { ErrorText, Sk, cx } from '../components/ui'
import ClassNotes from './ClassNotes'
import NotFound from './NotFound'
import { Icon, RoundButton, useBack, useNow, useTimetable } from './timetable'

export default function Course() {
  const { courseId } = useParams()
  const { me, tt, sessions, error, markSeen, retry } = useTimetable()
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
  // The coming seven days: weekly classes, plus dated ones (timetables that change week to week).
  const mine = inWeek(all, new Date(now.getFullYear(), now.getMonth(), now.getDate()))
  const nowMin = nowMinutes(now)
  const live = mine.find((s) => onDate(s, now) && s.start <= nowMin && nowMin < s.end)
  // The next session from now.
  const next = all
    .filter((s) => s !== live)
    .map((s) => ({ s, wait: until(s, now) }))
    .filter((x) => x.wait < Infinity)
    .sort((a, b) => a.wait - b.wait)[0]?.s
  const types = [...new Set(mine.map((s) => TYPE_NAME[s.type]))].join(' & ')

  if (!error && me && tt && !cls) return <NotFound course />

  return (
    <div className="min-h-dvh bg-sky text-navy">
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
                <p className="label text-[10px]">Changed {new Date(cls.section.changedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</p>
                <p className="mt-1 text-[14px] font-semibold">{cls.section.changeSummary}</p>
              </div>
            )}

            <section className="mx-3 mt-[18px] mb-2 flex flex-1 flex-col gap-[18px] rounded-[32px] bg-navy p-5 text-cream">
              <div className="flex flex-col gap-2">
                <h2 className="text-[18px] font-extrabold">When &amp; where</h2>
                <ul className="flex flex-col gap-2">
                  {mine.map((s, i) => {
                    const isLive = s === live
                    const isNext = s === next && !live
                    return (
                      <li
                        key={i}
                        className={cx('grid grid-cols-[56px_minmax(0,1fr)_auto] items-center gap-3 rounded-[18px] px-3.5 py-3', isLive ? 'bg-sky text-navy' : 'bg-navy-800')}
                      >
                        <span className={cx('label text-[12px] tracking-normal', !isLive && 'text-ice')}>{DAY_SHORT[s.meeting.day]}</span>
                        <span className="flex min-w-0 flex-col gap-0.5">
                          <span className="text-[18px] leading-none font-extrabold tabular-nums">
                            {s.meeting.start}–{s.meeting.end}
                          </span>
                          <span className={cx('text-[13px] font-semibold', !isLive && 'text-ice')}>
                            {s.meeting.room ? `Room ${s.meeting.room}` : 'Room TBA'} · {TYPE_NAME[s.type]}
                          </span>
                        </span>
                        <span className="flex flex-col items-end gap-1">
                          {isLive && <span className="label rounded-full bg-navy px-2 py-1 text-[10px] text-sky">● Now</span>}
                          {isNext && <span className="label text-[10px] text-ice">Next</span>}
                          {s.clash && <span className="label rounded-full bg-alert-ink px-2 py-1 text-[10px] text-cream">Clash</span>}
                        </span>
                      </li>
                    )
                  })}
                </ul>
              </div>

              <div className="flex items-center gap-3">
                <span aria-hidden="true" className="grid size-11 shrink-0 place-items-center rounded-full bg-ice text-[16px] font-extrabold text-navy">
                  {cls.section.instructor ? initials(cls.section.instructor) : '?'}
                </span>
                <span className="flex min-w-0 flex-col gap-0.5">
                  <span className="text-[16px] font-extrabold">{cls.section.instructor || 'Lecturer to be announced'}</span>
                  <span className="label text-[11px] tracking-normal text-ice">Lecturer</span>
                </span>
              </div>

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

/** Minutes from now until the session next starts: round the week for weekly classes, Infinity for a dated class that's over. */
function until(s: Session, now: Date) {
  const nowMin = nowMinutes(now)
  if (s.meeting.date) {
    const [y, m, d] = s.meeting.date.split('-').map(Number)
    const days = Math.round((new Date(y, m - 1, d).getTime() - new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()) / 86_400_000)
    const wait = days * 1440 + s.start - nowMin
    return wait > 0 ? wait : Infinity
  }
  let days = (s.meeting.day - now.getDay() + 7) % 7
  if (days === 0 && s.start <= nowMin) days = 7
  return days * 1440 + s.start - nowMin
}
