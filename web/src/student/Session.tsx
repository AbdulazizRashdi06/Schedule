import { useState } from 'react'
import { ENABLE_MESSAGE, enableDevice, useNotifySettings, type PushBase } from '../lib/push'
import { Link } from 'react-router-dom'
import { DAY_LONG, DAY_SHORT } from '../lib/time'
import { TYPE_NAME, addDays, changeTag, clashOn, fmtDate, fmtDay, isoDate, nowMinutes, onDate, sessionCode, type Session } from '../lib/slots'
import { cx } from '../components/ui'
import { counts, heldSoFar, useAbsences } from '../lib/absences'
import type { TeachMe, Timetable, Weeks } from '../lib/types'
import type { Viewer } from '../lib/viewer'
import ClassNotes from './ClassNotes'
import ClassTeachers from './ClassTeachers'
import SessionActions from '../teacher/SessionActions'
import type { ChangeDone } from '../teacher/ChangeSheet'
import { Icon, RoundButton } from './timetable'

const DAY_MS = 86_400_000

/** How far a date is from today, in words: "Today", "Tomorrow", "In 3 days", "2 days ago". */
function when(date: Date, now: Date) {
  const days = Math.round((date.getTime() - new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()) / DAY_MS)
  if (days === 0) return 'Today'
  if (days === 1) return 'Tomorrow'
  if (days === -1) return 'Yesterday'
  return days > 0 ? `In ${days} days` : `${-days} days ago`
}

/**
 * The class's next meeting after this one: later the same day, or on a following day (up to ten weeks on). A student's
 * class is their course; a teacher can teach two sections of one course, so theirs is the section.
 */
function nextAfter(s: Session, date: Date, all: Session[], bySection: boolean) {
  const same = (x: Session) => (bySection ? x.cls.section.id === s.cls.section.id : x.cls.courseId === s.cls.courseId)
  for (let i = 0; i <= 70; i++) {
    const d = addDays(date, i)
    const found = all
      .filter((x) => same(x) && onDate(x, d) && (i > 0 || x.start > s.start))
      .sort((a, b) => a.start - b.start)[0]
    if (found) return { s: found, date: d }
  }
  return null
}

/**
 * One session of a class: this lecture, lab or tutorial on this date. The course page's design, with this session
 * and the course's next one in place of the full list of times, and notes that switch between this session's and the
 * whole course's.
 */
export default function SessionPage({
  s,
  date,
  now,
  all,
  ghosts,
  tt,
  me,
  link,
  onBack,
  onChanged,
  afterChange,
  weeks,
  viewer,
  teach,
  offline,
}: {
  s: Session
  date: Date
  now: Date
  all: Session[]
  /** Cancelled classes and the old slots of moved ones, to link a moved class to its other half. */
  ghosts: Session[]
  tt: Timetable | null
  me: string
  link: (s: Session, date: Date) => string
  onBack: () => void
  onChanged: () => void
  /** Teacher side: called after a change to this class was made, edited or undone. */
  afterChange: (done: ChangeDone) => void
  /** The semester's teaching weeks, to count classes held so far. */
  weeks?: Weeks
  /** Whose home this is: a student's own classes, or the classes a teacher teaches. */
  viewer: Viewer
  /** On the teacher side, who is using it. */
  teach: TeachMe | null
  /** Set while the home shows a saved copy: nothing can be changed then. */
  offline: Date | null
}) {
  const cls = s.cls
  const nowMin = nowMinutes(now)
  const live = !s.off && isoDate(date) === isoDate(now) && s.start <= nowMin && nowMin < s.end
  const student = viewer.mode === 'student'
  const next = nextAfter(s, date, all, !student)
  // Reminders are the person's own: an owner viewing as a teacher has none to set.
  const reminders = student || !!teach?.personal

  return (
    <div className="min-h-dvh bg-sky text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(1rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3 px-6">
          <RoundButton label="Back" onClick={onBack} className="bg-navy text-sky">
            <Icon name="back" />
          </RoundButton>
          <span className={cx('label rounded-full border-[2.5px] border-navy px-3 py-2 text-[12px] tracking-[0.14em]', s.merged && 'min-w-0 text-right leading-[1.5] tracking-[0.08em]')}>
            {s.merged ? (
              // One real class under several course codes (teacher side).
              sessionCode(s, date)
            ) : (
              <>
                {cls.code}
                {cls.section.number && ` · Section ${cls.section.number}`}
              </>
            )}
          </span>
        </header>

        <div className="flex flex-col gap-1.5 px-6 pt-[18px]">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.04em] text-balance break-words hyphens-auto">{cls.name}</h1>
          <p className="text-[15px] font-semibold">
            {[`${DAY_LONG[date.getDay()]} ${fmtDate(date)}`, TYPE_NAME[s.type], cls.credits != null && `${cls.credits} ${cls.credits === 1 ? 'credit' : 'credits'}`]
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

        <ChangeBanner s={s} all={all} ghosts={ghosts} link={link} />

        <section className="mx-3 mt-[18px] mb-2 flex flex-1 flex-col gap-[18px] rounded-[32px] bg-navy p-5 text-cream">
          {s.plannerNotes && <p className="rounded-[14px] bg-navy-800 p-3 text-[14px] font-semibold whitespace-pre-wrap">{s.plannerNotes}</p>}
          <div className="flex flex-col gap-2">
            <h2 className="text-[18px] font-extrabold">This session</h2>
            <Row s={s} date={date} live={live} badge={live ? '● Now' : when(date, now)} />
            {/* A class that isn't on can't be missed. */}
            {student && counts(s) && !s.off && <Attendance s={s} date={date} now={now} all={all} weeks={weeks} />}
            {!student && <SessionActions s={s} date={date} now={now} tt={tt} teach={teach} offline={offline} afterChange={afterChange} />}
          </div>

          {next && (
            <div className="flex flex-col gap-2">
              <h2 className="text-[18px] font-extrabold">Next session</h2>
              <Link to={link(next.s, next.date)} className="press block rounded-[18px]">
                <Row s={next.s} date={next.date} live={false} badge={when(next.date, now)} arrow />
              </Link>
            </div>
          )}

          {/* One real class under several codes (teacher side): everyone who teaches any of them. */}
          <ClassTeachers cls={s.merged ? { ...cls, teachers: s.merged.flatMap((m) => m.cls.teachers ?? []).filter((t, i, list) => list.findIndex((x) => x.key === t.key) === i) } : cls} />

          {/* Students' notes are theirs: teachers don't see them. */}
          {student && <ClassNotes key={isoDate(date)} courseId={cls.courseId} section={cls.section} me={me} onChanged={onChanged} session={isoDate(date)} />}

          {reminders && <RemindButton base={viewer.base} />}
        </section>
      </div>
    </div>
  )
}

/**
 * Your own absence count for the class, and "I missed this" for a session that's on or over. Against the university's
 * attendance limit when one is set, warning as you get close.
 */
function Attendance({ s, date, now, all, weeks }: { s: Session; date: Date; now: Date; all: Session[]; weeks?: Weeks }) {
  const { limit, absences, ready, mark } = useAbsences()
  const [error, setError] = useState('')
  const iso = isoDate(date)
  const started = iso < isoDate(now) || (iso === isoDate(now) && s.start <= nowMinutes(now))
  const course = absences.filter((a) => a.courseId === s.cls.courseId)
  const missed = course.some((a) => a.date === iso && a.start === s.meeting.start)
  const held = heldSoFar(s.cls.courseId, all, weeks, now)
  const pct = held ? Math.round((course.length / held) * 100) : null
  const close = limit !== null && pct !== null && pct >= limit - 5
  if (!ready) return null
  return (
    <div className={cx('flex items-center justify-between gap-3 rounded-[18px] px-3.5 py-3', close ? 'bg-alert-bg text-alert-ink' : 'bg-navy-800')}>
      <span className="flex min-w-0 flex-col gap-0.5">
        <span className="text-[16px] leading-tight font-extrabold">
          {course.length === 0 ? (
            'No classes missed'
          ) : (
            <>
              Missed <span key={course.length} className="pop tabular-nums">{course.length}</span>
              {held !== null && ` of ${held}`}
            </>
          )}
        </span>
        <span className={cx('text-[13px] font-semibold', !close && 'text-ice')} role="status">
          {error ||
            (limit !== null && pct !== null
              ? close
                ? `${pct}%, close to the ${limit}% limit`
                : `${pct}% so far · limit ${limit}%`
              : 'Your own count. Only you see it.')}
        </span>
      </span>
      {started && (
        <button
          type="button"
          aria-pressed={missed}
          onClick={() => {
            setError('')
            mark(s.cls.courseId, date, s.meeting.start, !missed).catch(() => setError('Didn’t save. Try again.'))
          }}
          className={cx(
            'press label flex h-10 shrink-0 items-center gap-1.5 rounded-full px-3.5 text-[11px]',
            missed ? 'bg-cream text-navy' : close ? 'shadow-[inset_0_0_0_2px_currentColor]' : 'text-cream shadow-[inset_0_0_0_2px_rgb(255_253_248/0.35)]',
          )}
        >
          {missed && <Icon name="check" className="pop size-3.5" strokeWidth={3} />}
          {missed ? 'Missed' : 'I missed this'}
        </button>
      )}
    </div>
  )
}

/**
 * "Remind me 15 min before": turns on notifications for this device and reminders for every class. Once on, it says so
 * and turns reminders off again; how early is chosen under Menu, Notifications.
 */
function RemindButton({ base }: { base: PushBase }) {
  const { settings, save, reload } = useNotifySettings(base)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const on = settings?.remindMinutes != null && settings.devices > 0
  const toggle = async () => {
    if (!settings || busy) return
    setBusy(true)
    setStatus('')
    try {
      if (on) {
        await save({ remindMinutes: null, changes: settings.changes })
        setStatus('Reminders off')
      } else {
        const r = await enableDevice(base)
        if (r !== 'on') return setStatus(ENABLE_MESSAGE[r])
        await save({ remindMinutes: settings.remindMinutes ?? 15, changes: settings.changes })
        await reload()
        setStatus('You’ll get a reminder before every class')
      }
    } catch {
      setStatus('That didn’t work. Try again.')
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="mt-auto flex flex-col gap-2">
      <button
        type="button"
        aria-pressed={on}
        disabled={!settings}
        onClick={toggle}
        className={cx(
          'press flex h-14 w-full shrink-0 items-center justify-center gap-2.5 rounded-full text-[17px] font-extrabold transition-colors duration-200',
          on ? 'bg-navy-800 text-cream shadow-[inset_0_0_0_2px_rgb(255_253_248/0.3)]' : 'bg-cream text-navy',
        )}
      >
        <Icon name="bell" />
        <span key={String(on)} className="pop">
          {on ? `Reminding you ${settings?.remindMinutes} min before` : `Remind me ${settings?.remindMinutes ?? 15} min before`}
        </span>
      </button>
      <p className="min-h-4 text-center text-[12px] font-semibold text-ice" role="status">
        {status && <span className="msg inline-block">{status}</span>}
      </p>
    </div>
  )
}

/** A session as the course page lists its times: day, time, room and type, and a tag on the right. */
function Row({ s, date, live, badge, arrow }: { s: Session; date: Date; live: boolean; badge: string; arrow?: boolean }) {
  const tag = changeTag(s)
  return (
    <div className={cx('grid grid-cols-[56px_minmax(0,1fr)_auto] items-center gap-3 rounded-[18px] px-3.5 py-3', live ? 'bg-sky text-navy' : 'bg-navy-800')}>
      <span className={cx('label flex flex-col text-[12px] tracking-normal', !live && 'text-ice')}>
        {DAY_SHORT[date.getDay()]}
        <span className="text-[10px] opacity-80">{fmtDate(date)}</span>
      </span>
      <span className="flex min-w-0 flex-col gap-0.5">
        <span className={cx('text-[18px] leading-none font-extrabold tabular-nums', s.off && 'text-ice/70 line-through decoration-2')}>
          {s.meeting.start}–{s.meeting.end}
        </span>
        <span className={cx('text-[13px] font-semibold', !live && 'text-ice')}>
          {s.meeting.room ? `Room ${s.meeting.room}` : 'Room TBA'} · {TYPE_NAME[s.type]}
        </span>
        {tag && <span className={cx('label mt-1 self-start rounded-full px-2 py-1 text-[10px]', s.off ? 'bg-cream text-navy' : 'bg-sky text-navy')}>{tag}</span>}
      </span>
      <span className="flex items-center gap-1.5">
        <span className={cx('flex flex-col items-end gap-1')}>
          <span className={cx('label rounded-full px-2 py-1 text-[10px]', live ? 'bg-navy text-sky' : 'text-ice')}>{badge}</span>

          {clashOn(s, date) && <span className="label rounded-full bg-alert-ink px-2 py-1 text-[10px] text-cream">Clash</span>}
        </span>
        {arrow && <Icon name="next" className="size-4 text-ice" />}
      </span>
    </div>
  )
}

/**
 * What a teacher's change did to this session, above the panel: who, what, their note, and for a moved class a link
 * to its other half (the old slot, or where it is now).
 */
function ChangeBanner({ s, all, ghosts, link }: { s: Session; all: Session[]; ghosts: Session[]; link: (s: Session, date: Date) => string }) {
  const c = s.change
  if (!c) return null
  const at = (x: { date: string; start: string; room: string }) => `${fmtDay(x.date)} ${x.start}`
  const room = (r: string) => r || 'a room to be announced'
  const label = s.off === 'cancelled' ? 'Cancelled' : c.kind === 'move' ? 'Moved' : c.kind === 'room' ? 'Room changed' : 'Extra class'
  const text =
    s.off === 'cancelled'
      ? `This class is cancelled. It isn’t on ${fmtDay(s.meeting.date ?? c.from?.date ?? '')}.`
      : s.off === 'moved'
        ? c.to
          ? `This class moved to ${at(c.to)}, in ${room(c.to.room)}. It isn’t on at this time.`
          : 'This class moved. It isn’t on at this time.'
        : c.kind === 'move'
          ? c.from
            ? `Moved here from ${at(c.from)}.`
            : 'This class was moved here.'
          : c.kind === 'room'
            ? `Now in ${room(c.to?.room ?? '')}${c.from?.room ? `, not ${c.from.room}` : ''}.`
            : 'An extra class, on this date only.'
  // The other half of a move: the same change (any of its codes, for one class under several).
  const ids = (s.merged ?? [s]).map((m) => m.change?.id)
  const other = c.kind === 'move' ? (s.off ? all : ghosts).find((x) => (x.merged ?? [x]).some((m) => m.change && ids.includes(m.change.id))) : undefined
  return (
    <div className="mx-3 mt-4 rounded-[18px] bg-cream px-4 py-3">
      <p className="label text-[10px]">
        {label} by {c.by}
      </p>
      <p className="mt-1 text-[14px] font-semibold">{text}</p>
      {c.note && <p className="mt-1.5 border-l-[2.5px] border-navy/25 pl-2.5 text-[14px] font-semibold whitespace-pre-wrap">{c.note}</p>}
      {other?.meeting.date && (
        <Link to={link(other, new Date(`${other.meeting.date}T12:00`))} replace className="label mt-1 flex min-h-11 items-center gap-1.5 text-[11px] link-hover">
          {s.off ? 'See it at its new time' : 'See the old time'} <span aria-hidden="true">→</span>
        </Link>
      )}
    </div>
  )
}
