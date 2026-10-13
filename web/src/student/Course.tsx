import { useEffect, useState } from 'react'
import { Link, Navigate, useParams } from 'react-router-dom'
import { DAY_LONG } from '../lib/time'
import { TYPE_NAME, initials, nowMinutes, type Session } from '../lib/slots'
import { ErrorText, Loading, cx } from '../components/ui'
import { Icon, RoundButton, useBack, useNow, useTimetable } from './timetable'

export default function Course() {
  const { courseId } = useParams()
  const { me, tt, sessions, error, markSeen } = useTimetable()
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

  const mine = sessions.filter((s) => s.cls.courseId === Number(courseId))
  const today = now.getDay()
  const nowMin = nowMinutes(now)
  const live = mine.find((s) => s.meeting.day === today && s.start <= nowMin && nowMin < s.end)
  // The next session from now, wrapping round the week.
  const next = [...mine]
    .filter((s) => s !== live)
    .sort((a, b) => order(a, today, nowMin) - order(b, today, nowMin))[0]
  const types = [...new Set(mine.map((s) => TYPE_NAME[s.type]))].join(' & ')

  return (
    <div className="min-h-dvh bg-sky font-display text-navy">
      <div className="mx-auto max-w-md px-6 pt-[max(1rem,env(safe-area-inset-top))] pb-[max(1.5rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3">
          <RoundButton label="Back" onClick={back} className="bg-navy text-cream hover:bg-navy-800">
            <Icon name="back" />
          </RoundButton>
          {cls && (
            <span className="label flex h-11 items-center rounded-full border-2 border-navy px-4 text-[11px]">
              {cls.code} · Section {cls.section.number}
            </span>
          )}
        </header>

        {error ? (
          <div className="mt-8">
            <ErrorText>{error}</ErrorText>
          </div>
        ) : !me || !tt ? (
          <Loading />
        ) : !cls ? (
          <div className="mt-10">
            <h1 className="display text-[44px] leading-[0.95] tracking-[-0.04em]">Not in your timetable.</h1>
            <Link to="/" className="mt-6 inline-flex h-14 items-center gap-2 rounded-full bg-navy px-6 text-[16px] font-semibold text-cream">
              Back to today <Icon name="arrow" className="size-4" />
            </Link>
          </div>
        ) : (
          <>
            <h1 className="display mt-8 text-[52px] leading-[0.95] tracking-[-0.04em] text-balance break-words hyphens-auto">{cls.name}</h1>
            <p className="mt-3 text-[15px] font-semibold">
              {[
                types || cls.type,
                `${mine.length}× a week`,
                cls.credits != null && `${cls.credits} ${cls.credits === 1 ? 'credit' : 'credits'}`,
              ]
                .filter(Boolean)
                .join(' · ')}
            </p>

            {cls.section.changeSummary && cls.section.changedAt && (
              <div className="mt-5 rounded-[18px] bg-cream p-4">
                <p className="label text-[10px]">Changed {new Date(cls.section.changedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</p>
                <p className="mt-1 text-[14px] font-semibold">{cls.section.changeSummary}</p>
              </div>
            )}

            <div className="mt-6 rounded-[32px] bg-navy p-6 text-cream">
              <h2 className="label text-[10px] text-ice">When &amp; where</h2>
              <ul className="mt-2">
                {mine.map((s, i) => {
                  const isLive = s === live
                  const isNext = s === next && !live
                  return (
                    <li key={i} className={cx('flex items-center gap-3 border-b border-navy-line py-3.5 last:border-0', isLive && 'text-sky')}>
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="text-[16px] font-semibold">{DAY_LONG[s.meeting.day]}</span>
                          {isLive && <span className="label text-[9px]">● Now</span>}
                          {isNext && <span className="label rounded-full bg-navy-700 px-2 py-0.5 text-[9px] text-sky">Next</span>}
                          {s.clash && <span className="label rounded-full bg-red-600 px-2 py-0.5 text-[9px] text-white">Clash</span>}
                        </div>
                        <div className={cx('label mt-1 text-[10px]', !isLive && 'text-ice')}>
                          {s.meeting.start} – {s.meeting.end} · {TYPE_NAME[s.type]}
                        </div>
                      </div>
                      <span className="flex shrink-0 items-center gap-1 text-[14px] font-semibold">
                        <Icon name="pin" className="size-3.5" />
                        {s.meeting.room || 'TBA'}
                      </span>
                    </li>
                  )
                })}
              </ul>

              <h2 className="label mt-6 text-[10px] text-ice">Lecturer</h2>
              <div className="mt-3 flex items-center gap-3">
                <span className="display grid size-12 shrink-0 place-items-center rounded-full bg-sky text-[16px] text-navy" aria-hidden="true">
                  {cls.section.instructor ? initials(cls.section.instructor) : '?'}
                </span>
                <span className="min-w-0 text-[16px] font-semibold">{cls.section.instructor || 'To be announced'}</span>
              </div>

              {/* Reminders aren't built yet: the button is in place and says so when tapped. */}
              <button
                type="button"
                onClick={() => setSoon(true)}
                className="mt-7 flex h-14 w-full items-center justify-center gap-2 rounded-full bg-cream text-[16px] font-semibold text-navy transition active:scale-[0.98]"
              >
                <Icon name="bell" className="size-4" />
                Remind me 15 min before
              </button>
              <p className="label mt-3 min-h-4 text-center text-[10px] text-ice" role="status">
                {soon && 'Reminders are coming soon'}
              </p>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

/** Minutes from now until the session next starts, counting round the week. */
function order(s: Session, today: number, nowMin: number) {
  let days = (s.meeting.day - today + 7) % 7
  if (days === 0 && s.start <= nowMin) days = 7
  return days * 1440 + s.start - nowMin
}
