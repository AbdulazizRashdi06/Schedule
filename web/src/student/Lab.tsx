import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react'
import { AnimatePresence, animate, motion, useScroll, useTransform, type MotionValue } from 'motion/react'
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import SessionPage from './Session'
import { DAY_LONG, DAY_SHORT, minutes } from '../lib/time'
import { SLOTS, TYPE_NAME, addDays, fmtWeekRange, inWeek, isoDate, notesOn, nowMinutes, onDate, weekDays, weekStart, weekTitle, type ClassType, type Session } from '../lib/slots'
import { tick } from '../lib/native'
import { Icon, cx } from '../components/ui'
import { store } from '../lib/api'
import Today, { DayHome, FitTitle } from './Today'
import { LabNav } from './labNav'
import { MenuSheet } from './StudentShell'
import SlotSheet, { type SlotPreset } from './SlotSheet'
import ShareSheet from './ShareSheet'
import type { CustomSlot } from '../lib/types'
import { useNow, useTimetable } from './timetable'

/** The weeks the page reaches when the semester's teaching weeks aren't set: last week to the week after next. */
const NEAR_WEEKS = [-1, 0, 1, 2]
const WEEK_MS = 7 * 86_400_000
const DAY_MS = 86_400_000
const ROW = 86
const GAP = 8
const NUDGE = 'lab-swipe-nudge'

/** Hides a strip's own scrollbar: the page itself is the position indicator. */
const noBar = '[scrollbar-width:none] [&::-webkit-scrollbar]:hidden'

/**
 * Mouse drags move a strip the way a finger does, so the page can be tried in a desktop browser too. A drag that turns
 * out to run along the other axis is left to the strip or page that scrolls that way. On release the strip carries on
 * with the drag's speed and the browser's snapping lands it on a page.
 */
function useMouseDrag(ref: RefObject<HTMLElement | null>, axis: 'x' | 'y', ready: boolean) {
  useEffect(() => {
    const el = ref.current
    if (!el) return
    let down = false
    let mine = false
    let dragged = false
    let sx = 0
    let sy = 0
    let from = 0
    let last = 0
    let lastT = 0
    let v = 0
    const along = (e: PointerEvent) => (axis === 'x' ? e.clientX : e.clientY)
    const onDown = (e: PointerEvent) => {
      if (e.pointerType !== 'mouse' || e.button !== 0) return
      down = true
      mine = false
      dragged = false
      sx = e.clientX
      sy = e.clientY
      last = along(e)
      lastT = e.timeStamp
      from = axis === 'x' ? el.scrollLeft : el.scrollTop
      v = 0
    }
    const onMove = (e: PointerEvent) => {
      if (!down) return
      if (!mine) {
        const dx = Math.abs(e.clientX - sx)
        const dy = Math.abs(e.clientY - sy)
        if (Math.max(dx, dy) < 6) return
        if ((axis === 'x') !== dx > dy) {
          down = false
          return
        }
        mine = true
        dragged = true
        el.style.scrollSnapType = 'none'
        el.setPointerCapture(e.pointerId)
      }
      const p = along(e)
      const dt = Math.max(1, e.timeStamp - lastT)
      v = 0.8 * ((p - last) / dt) + 0.2 * v
      last = p
      lastT = e.timeStamp
      const to = from - (p - (axis === 'x' ? sx : sy))
      if (axis === 'x') el.scrollLeft = to
      else el.scrollTop = to
    }
    const onUp = () => {
      if (!down) return
      down = false
      if (!mine) return
      const at = axis === 'x' ? el.scrollLeft : el.scrollTop
      const moved = at - from
      const goes = Math.abs(moved) > 0 ? Math.sign(moved) : 0
      // One page per drag, like a finger: past a third of the way, or flicked, it goes on; otherwise it settles back.
      const commit = goes !== 0 && (Math.abs(moved) > step() / 3 || Math.abs(v) > 0.35)
      const target = snapPoint(from, goes, commit)
      el.scrollTo(axis === 'x' ? { left: target, behavior: 'smooth' } : { top: target, behavior: 'smooth' })
      let done = false
      const restore = () => {
        if (done) return
        done = true
        el.removeEventListener('scrollend', restore)
        // Land exactly on the page before snapping comes back, so it never rests between two.
        const now = axis === 'x' ? el.scrollLeft : el.scrollTop
        if (Math.abs(now - target) > 1) el.scrollTo(axis === 'x' ? { left: target, behavior: 'instant' } : { top: target, behavior: 'instant' })
        el.style.scrollSnapType = ''
      }
      el.addEventListener('scrollend', restore)
      setTimeout(restore, 1000)
    }
    const step = () => (axis === 'x' ? el.clientWidth : el.clientHeight)
    /** Where a drag from `start` lands: the page it started on, or the next one in the direction it went. */
    const snapPoint = (start: number, dir: number, commit: boolean) => {
      if (axis === 'x') {
        const i = Math.round(start / el.clientWidth) + (commit ? dir : 0)
        return Math.max(0, Math.min(el.scrollWidth - el.clientWidth, i * el.clientWidth))
      }
      // Down the page the stops are the day and the top of the week; inside a long week it just carries on.
      const stops = [...el.querySelectorAll<HTMLElement>(':scope > section')].map((s) => s.offsetTop)
      const last = stops[stops.length - 1] ?? 0
      if (start >= last && el.scrollTop >= last) return Math.max(last, el.scrollTop - v * 220)
      const i = Math.max(0, stops.findIndex((s, k) => start < (stops[k + 1] ?? Infinity) && start >= s))
      return stops[Math.max(0, Math.min(stops.length - 1, i + (commit ? dir : 0)))]
    }
    const onClick = (e: MouseEvent) => {
      if (!dragged) return
      dragged = false
      e.preventDefault()
      e.stopPropagation()
    }
    el.addEventListener('pointerdown', onDown)
    el.addEventListener('pointermove', onMove)
    el.addEventListener('pointerup', onUp)
    el.addEventListener('pointercancel', onUp)
    el.addEventListener('click', onClick, true)
    return () => {
      el.removeEventListener('pointerdown', onDown)
      el.removeEventListener('pointermove', onMove)
      el.removeEventListener('pointerup', onUp)
      el.removeEventListener('pointercancel', onUp)
      el.removeEventListener('click', onClick, true)
    }
  }, [ref, axis, ready])
}

/**
 * A page in a sideways strip. As it slides, its content trails a little behind, shrinks a touch and fades toward the
 * edges, so the strip reads as one surface moving under your finger rather than pages swapping.
 */
/** How many pages either side of the one you're on are drawn; further ones are empty until you get near. */
const REACH = 5

/** A page too far from where you are to be seen yet: holds its place in the strip, draws nothing. */
const Far = () => <div className="w-full shrink-0 snap-start snap-always" />

function Slide({ strip, active, children }: { strip: RefObject<HTMLDivElement | null>; active: boolean; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const { scrollXProgress } = useScroll({ container: strip, target: ref, axis: 'x', offset: ['start end', 'end start'] })
  const x = useTransform(scrollXProgress, [0, 0.5, 1], ['16%', '0%', '-16%'])
  const scale = useTransform(scrollXProgress, [0, 0.5, 1], [0.92, 1, 0.92])
  const opacity = useTransform(scrollXProgress, [0, 0.5, 1], [0.25, 1, 0.25])
  return (
    // Off-screen pages are inert: out of the tab order and hidden from screen readers, not just aria-hidden.
    <div ref={ref} inert={!active} className="w-full shrink-0 snap-start snap-always">
      <motion.div style={{ x, scale, opacity, transformOrigin: '50% 20%' }}>{children}</motion.div>
    </div>
  )
}

/** Today's bar, the longest; each step away is shorter, down to a stub. */
const longest = 30
const barLength = (steps: number) => Math.max(3, longest - steps * 6)
const BAR_GAP = 5

/**
 * Where you are along a strip, in the setup screen's progress bars. The row stands still with today (or this week) in
 * the middle as the longest bar; the further a day is from today, the shorter its bar, so the stubs at the ends say
 * how far out you can go. The bar for the page you're on fills navy, and the fill moves along as you swipe.
 */
function Bars({ strip, labels, mark, onPick, label }: { strip: RefObject<HTMLDivElement | null>; labels: string[]; mark: number; onPick: (i: number) => void; label: string }) {
  const { scrollX } = useScroll({ container: strip })
  const pos = useTransform(scrollX, (v) => v / (strip.current?.clientWidth || 1))
  const tick = (i: number) => <Tick key={labels[i]} i={i} pos={pos} label={labels[i]} length={barLength(Math.abs(i - mark))} onPick={() => onPick(i)} />
  // Only as many bars as can show: past about 18 steps the stubs are off the faded ends anyway.
  const before = labels.map((_, i) => i).filter((i) => i < mark && mark - i <= 18)
  const after = labels.map((_, i) => i).filter((i) => i > mark && i - mark <= 18)
  return (
    // Two equal sides around today keep it in the middle however many days lie on each side.
    <div aria-label={label} className="grid grid-cols-[1fr_auto_1fr] items-center" style={{ columnGap: BAR_GAP, width: 300 }}>
      {/* A whole semester has more days than fit: the far stubs run on past the ends and fade out there. */}
      <div className="flex justify-end overflow-hidden [mask-image:linear-gradient(to_right,transparent,black_45%)]" style={{ gap: BAR_GAP }}>
        {before.map(tick)}
      </div>
      {tick(mark)}
      <div className="flex justify-start overflow-hidden [mask-image:linear-gradient(to_left,transparent,black_45%)]" style={{ gap: BAR_GAP }}>
        {after.map(tick)}
      </div>
    </div>
  )
}

/** One bar, filling navy as you reach its page. */
function Tick({ i, pos, label, length, onPick }: { i: number; pos: MotionValue<number>; label: string; length: number; onPick: () => void }) {
  const fill = useTransform(pos, (p) => Math.max(0, 1 - Math.abs(i - p) * 1.6))
  return (
    <button type="button" aria-label={label} onClick={onPick} className="pointer-events-auto grid h-8 shrink-0 place-items-center" style={{ width: length }}>
      <span className="relative h-1.5 w-full overflow-hidden rounded-[3px] bg-navy/[0.12]">
        <motion.span className="absolute inset-0 bg-navy" style={{ opacity: fill }} />
      </span>
    </button>
  )
}

/** Calls `fn` once a strip has come to rest. */
function onSettle(fn: () => void) {
  let t: ReturnType<typeof setTimeout> | undefined
  return () => {
    clearTimeout(t)
    t = setTimeout(fn, 90)
  }
}

/**
 * The home page: one page you move around in, instead of pages that replace each other. Across the top, the home screen for
 * each day side by side: Today, then Tomorrow, and so on. Scroll down and the same page carries on into the week.
 * Every move uses the phone's own scrolling, snapped to a day or the week.
 */
export default function Lab() {
  const { me, tt, sessions: all, retry, markSeen } = useTimetable()
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const location = useLocation()
  const now = useNow()
  const [menu, setMenu] = useState(false)
  const [editing, setEditing] = useState<CustomSlot | null>(null)
  const [adding, setAdding] = useState<SlotPreset | null>(null)
  const [sharing, setSharing] = useState(false)
  const page = useRef<HTMLDivElement>(null)
  const daysStrip = useRef<HTMLDivElement>(null)
  const weeksStrip = useRef<HTMLDivElement>(null)
  const weekSection = useRef<HTMLElement>(null)
  const daysSection = useRef<HTMLElement>(null)
  const lastPageTop = useRef(0)

  const todayIso = isoDate(now)
  const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const weekOf = useCallback((d: Date) => Math.round((weekStart(d).getTime() - weekStart(now).getTime()) / WEEK_MS), [todayIso])

  // The whole semester, week 1 to its last week, when its teaching weeks are set; this week is always in reach.
  const sem = me?.semester?.weeks
  const WEEKS = useMemo(() => {
    if (!sem?.week1Start || !sem.lastWeek) return NEAR_WEEKS
    const first = weekOf(new Date(`${sem.week1Start}T00:00`))
    const out = Array.from({ length: sem.lastWeek }, (_, i) => first + i)
    if (!out.includes(0)) out.push(0)
    return out.sort((a, b) => a - b)
  }, [sem?.week1Start, sem?.lastWeek, weekOf])

  // Every teaching day in reach, plus today even when it's a weekend.
  const dates = useMemo(() => {
    const out: Date[] = []
    for (const o of WEEKS) {
      const start = weekStart(now, o)
      for (const d of weekDays(inWeek(all, start))) out.push(addDays(start, d))
    }
    if (!out.some((d) => isoDate(d) === todayIso)) out.push(new Date(now.getFullYear(), now.getMonth(), now.getDate()))
    return out.sort((a, b) => a.getTime() - b.getTime())
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [all, todayIso, WEEKS])
  const todayIndex = Math.max(0, dates.findIndex((d) => isoDate(d) === todayIso))

  const [dayIndex, setDayIndex] = useState(todayIndex)
  const [week, setWeek] = useState(0)
  // Share sends the week you're looking at.
  const shareStart = weekStart(now, week)
  const shareSessions = inWeek(all, shareStart)
  const ready = Boolean(tt)

  useMouseDrag(page, 'y', ready)
  useMouseDrag(daysStrip, 'x', ready)
  useMouseDrag(weeksStrip, 'x', ready)

  // Scrolling down, the day sinks back and fades as the week comes up over it; scrolling up, the reverse.
  const { scrollYProgress: leaving } = useScroll({ container: page, target: daysSection, offset: ['start start', 'end start'] })
  const dayY = useTransform(leaving, [0, 1], ['0%', '38%'])
  const dayScale = useTransform(leaving, [0, 1], [1, 0.94])
  const { scrollYProgress: arriving } = useScroll({ container: page, target: weekSection, offset: ['start end', 'start start'] })
  // Resting on a day there is no mist over it; it gathers only as the week comes up.
  const mist = useTransform(arriving, [0, 0.45], [0, 1])

  // Once, the first time: the days give a small sideways nudge, so it's clear they slide.
  useEffect(() => {
    if (!ready) return
    let seen = true
    try {
      seen = store.get(NUDGE) === '1'
    } catch {
      /* no storage: skip the nudge */
    }
    if (seen) return
    const t = setTimeout(() => {
      const strip = daysStrip.current
      if (!strip) return
      store.set(NUDGE, '1')
      const base = strip.scrollLeft
      strip.style.scrollSnapType = 'none'
      const move = (v: number) => {
        strip.scrollLeft = base + v
      }
      void animate(0, 64, { type: 'spring', visualDuration: 0.35, bounce: 0, onUpdate: move }).then(() =>
        animate(64, 0, { type: 'spring', visualDuration: 0.45, bounce: 0.2, onUpdate: move }).then(() => {
          strip.style.scrollSnapType = ''
        }),
      )
    }, 1100)
    return () => clearTimeout(t)
  }, [ready])

  const showDay = useCallback((i: number, behavior: ScrollBehavior) => {
    const strip = daysStrip.current
    if (strip) strip.scrollTo({ left: i * strip.clientWidth, behavior })
  }, [])
  const showWeek = useCallback((o: number, behavior: ScrollBehavior) => {
    const strip = weeksStrip.current
    if (strip) strip.scrollTo({ left: WEEKS.indexOf(o) * strip.clientWidth, behavior })
  }, [WEEKS])

  // The date and week on screen, by value: the strips are lined up from these, and the settle handlers compare with
  // them, so nothing has to run inside a state update.
  const shownIso = useRef(todayIso)
  const shownWeek = useRef(0)

  /** Puts a day on screen in both strips' terms: the days on it, the week below on its week. */
  const land = useCallback(
    (i: number, dayBehavior: ScrollBehavior | null) => {
      const d = dates[i]
      if (!d) return
      if (dayBehavior) showDay(i, dayBehavior)
      shownIso.current = isoDate(d)
      setDayIndex(i)
      const o = weekOf(d)
      if (o !== shownWeek.current || dayBehavior === 'instant') {
        shownWeek.current = o
        setWeek(o)
        if (WEEKS.includes(o)) showWeek(o, 'instant')
      }
    },
    [dates, weekOf, showDay, showWeek, WEEKS],
  )

  // Open on today. When the days change later (adding a slot can add a Friday, the semester's weeks can arrive), stay
  // on the date you were on rather than whatever now sits at the same position.
  useLayoutEffect(() => {
    if (!ready) return
    const i = dates.findIndex((d) => isoDate(d) === shownIso.current)
    land(i >= 0 ? i : todayIndex, 'instant')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready, dates])

  // Moving through days keeps the week below on the same week.
  const daysSettled = useMemo(
    () =>
      onSettle(() => {
        const strip = daysStrip.current
        const i = strip ? Math.round(strip.scrollLeft / strip.clientWidth) : -1
        // A pause during a touch isn't a settled page. Changing inert pages mid-swipe can interrupt native snapping.
        if (strip && Math.abs(strip.scrollLeft - i * strip.clientWidth) > 1) return
        if (!strip || !dates[i] || isoDate(dates[i]) === shownIso.current) return
        tick('select')
        land(i, null)
      }),
    [dates, land],
  )
  const weeksSettled = useMemo(
    () =>
      onSettle(() => {
        const strip = weeksStrip.current
        const i = strip ? Math.round(strip.scrollLeft / strip.clientWidth) : -1
        if (strip && Math.abs(strip.scrollLeft - i * strip.clientWidth) > 1) return
        const o = WEEKS[i]
        if (o === undefined || o === shownWeek.current) return
        tick('select')
        shownWeek.current = o
        setWeek(o)
      }),
    [WEEKS],
  )

  // Once the week covers the day view, prepare today above it. Browsing weeks leaves it there,
  // so scrolling back up always returns to today; tapping a date still opens that specific day.
  const pageScrolled = () => {
    const top = page.current?.scrollTop ?? 0
    const boundary = (weekSection.current?.offsetTop ?? Infinity) - 1
    const enteredWeek = lastPageTop.current < boundary && top >= boundary
    lastPageTop.current = top
    if (!enteredWeek || !ready || shownIso.current === todayIso) return
    shownIso.current = todayIso
    setDayIndex(todayIndex)
    showDay(todayIndex, 'instant')
  }

  const toWeek = useCallback(() => {
    const el = weekSection.current
    if (el) page.current?.scrollTo({ top: el.offsetTop, behavior: 'smooth' })
  }, [])

  /** Up to a day: its page is lined up while out of sight, then the page scrolls up to it. */
  const toDay = useCallback(
    (date: Date, behavior: ScrollBehavior = 'instant') => {
      const i = dates.findIndex((d) => isoDate(d) === isoDate(date))
      if (i < 0) return
      land(i, behavior)
      page.current?.scrollTo({ top: 0, behavior: 'smooth' })
    },
    [dates, land],
  )

  /** "+ Add a slot" on a day: starts at that day's first free slot. */
  const addOn = useCallback(
    (date: Date) => {
      const taken = all.filter((s) => onDate(s, date))
      const free = SLOTS.findIndex((_, i) => !taken.some((s) => s.slot <= i && i < s.slot + s.span))
      const [start, end] = SLOTS[Math.max(0, free)]
      setAdding({ day: date.getDay(), date: isoDate(date), start, end })
    },
    [all],
  )

  /** A class on a date opens its own session page over the lab, kept in the address so Back closes it. */
  const sessionLink = useCallback(
    (s: Session, date: Date = now) => {
      const p = new URLSearchParams()
      const pin = params.get('now')
      if (pin) p.set('now', pin)
      p.set('session', `${s.cls.courseId}.${isoDate(date)}.${s.meeting.start}`)
      return `/?${p}`
    },
    [now, params],
  )

  const nav = useMemo(
    () => ({ week: toWeek, day: (d: Date) => toDay(d, 'smooth'), menu: () => setMenu(true), add: addOn, edit: setEditing, session: sessionLink }),
    [toWeek, toDay, addOn, sessionLink],
  )

  // The open session, from the address: course, date and start time.
  const open = useMemo(() => {
    const [id, iso, at] = (params.get('session') ?? '').split('.')
    if (!id || !iso) return null
    const [y, m, d] = iso.split('-').map(Number)
    const date = new Date(y, m - 1, d)
    const onDay = all.filter((s) => s.cls.courseId === Number(id) && onDate(s, date))
    const s = onDay.find((x) => x.meeting.start === at) ?? onDay[0]
    return s ? { s, date } : null
  }, [params, all])

  // Opening a class that changed counts as having seen the change.
  const seenId = open?.s.cls.changed ? open.s.cls.section.id : null
  useEffect(() => {
    if (seenId) markSeen(seenId)
  }, [seenId, markSeen])

  const closeSession = () => {
    if (location.key !== 'default') navigate(-1)
    else {
      const p = new URLSearchParams(params)
      p.delete('session')
      setParams(p, { replace: true })
    }
  }

  return (
    <LabNav.Provider value={nav}>
      <div ref={page} onScroll={pageScrolled} className={cx('h-dvh snap-y snap-mandatory overflow-y-auto overscroll-y-contain bg-cream text-navy', noBar)}>
        <section ref={daysSection} className="snap-start" aria-label="Days">
          <motion.div className="relative" style={{ y: dayY, scale: dayScale, transformOrigin: '50% 0%' }}>
            {ready && dates.length > 1 && (
              <div className="pointer-events-none absolute inset-x-0 top-[max(0px,calc(env(safe-area-inset-top)-0.5rem))] z-10 flex justify-center">
                <Bars
                  strip={daysStrip}
                  labels={dates.map((d) => `${DAY_LONG[d.getDay()]} ${d.getDate()} ${d.toLocaleString('en', { month: 'short' })}`)}
                  mark={todayIndex}
                  onPick={(i) => showDay(i, 'smooth')}
                  label="Days"
                />
              </div>
            )}
            {ready ? (
              <div ref={daysStrip} onScroll={daysSettled} className={cx('flex snap-x snap-mandatory overflow-x-auto overflow-y-hidden overscroll-x-contain', noBar)}>
                {dates.map((d, i) =>
                  Math.abs(i - dayIndex) > REACH ? (
                    <Far key={isoDate(d)} />
                  ) : (
                    <Slide key={isoDate(d)} strip={daysStrip} active={i === dayIndex}>
                      {i === todayIndex ? <Today /> : <DayHome date={d} offset={Math.round((d.getTime() - midnight.getTime()) / DAY_MS)} sessions={all} />}
                    </Slide>
                  ),
                )}
              </div>
            ) : (
              <Today />
            )}
          </motion.div>
        </section>

        <section ref={weekSection} className="relative z-10 min-h-dvh snap-start bg-cream" aria-label="Week">
          {/*
            No seam: a tall cream mist above the week swallows the day as the week rises. The day keeps its own colours
            underneath (fading it would turn the navy card and its lines grey).
          */}
          <motion.span
            aria-hidden="true"
            style={{ opacity: mist }}
            className="pointer-events-none absolute inset-x-0 bottom-full h-[45dvh] bg-linear-to-b from-cream/0 via-cream/70 to-cream"
          />
          {ready && (
            <div className="pointer-events-none absolute inset-x-0 top-[max(0px,calc(env(safe-area-inset-top)-0.5rem))] z-10 flex justify-center">
              <Bars strip={weeksStrip} labels={WEEKS.map((o) => weekTitle(o, weekStart(now, o), sem))} mark={WEEKS.indexOf(0)} onPick={(i) => showWeek(WEEKS[i], 'smooth')} label="Weeks" />
            </div>
          )}
          {ready && (
            <div ref={weeksStrip} onScroll={weeksSettled} className={cx('flex snap-x snap-mandatory overflow-x-auto overflow-y-hidden overscroll-x-contain', noBar)}>
              {WEEKS.map((o, j) =>
                Math.abs(j - WEEKS.indexOf(week)) > REACH ? (
                  <Far key={o} />
                ) : (
                <Slide key={o} strip={weeksStrip} active={o === week}>
                  <WeekPage
                    offset={o}
                    now={now}
                    all={all}
                    title={weekTitle(o, weekStart(now, o), sem)}
                    selected={dates[dayIndex]}
                    onDay={toDay}
                    onOpen={(s, d) => navigate(sessionLink(s, d))}
                    onAdd={setAdding}
                    onEdit={setEditing}
                    onShare={() => setSharing(true)}
                    arriving={arriving}
                  />
                </Slide>
                ),
              )}
            </div>
          )}
        </section>
      </div>
      {/* A session slides in from the side over the page, and back out, so where you were on the page is kept. */}
      <AnimatePresence>
        {open && (
          <motion.div
            key="session"
            initial={{ x: '100%' }}
            animate={{ x: 0 }}
            exit={{ x: '100%' }}
            transition={{ type: 'spring', visualDuration: 0.35, bounce: 0 }}
            className="fixed inset-0 z-40 overflow-y-auto overscroll-contain bg-sky shadow-[-24px_0_48px_-24px_rgb(11_25_87/0.5)]"
          >
            <SessionPage s={open.s} date={open.date} now={now} all={all} me={me?.universityId ?? ''} link={sessionLink} onBack={closeSession} onChanged={() => void retry()} />
          </motion.div>
        )}
      </AnimatePresence>
      <MenuSheet open={menu} onClose={() => setMenu(false)} me={me} />
      <SlotSheet
        slot={editing}
        preset={adding}
        onClose={() => {
          setEditing(null)
          setAdding(null)
        }}
        onSaved={() => void retry()}
      />
      {ready && (
        <ShareSheet
          open={sharing}
          onClose={() => setSharing(false)}
          input={{ sessions: shareSessions, days: weekDays(shareSessions), start: shareStart, title: weekTitle(week, shareStart, sem), range: fmtWeekRange(shareStart, weekDays(shareSessions).length) }}
        />
      )}
    </LabNav.Provider>
  )
}

/** On a cream page lectures are navy so they stand out; labs, tutorials and your own slots keep their colours. */
const BLOCK: Record<ClassType, string> = {
  lecture: 'bg-navy text-cream',
  lab: 'bg-ice text-navy',
  tutorial: 'bg-sky text-navy',
  mine: 'bg-paper text-navy outline-2 outline-dashed -outline-offset-2 outline-navy/50',
}

const HEADLINE: Record<number, string> = { [-1]: 'Last week.', 0: 'This week.', 1: 'Next week.', 2: 'In two weeks.' }

/** Where the current time sits on the slot grid, or null outside the teaching day. */
function nowY(nowMin: number) {
  for (let i = 0; i < SLOTS.length; i++) {
    const s = minutes(SLOTS[i][0])
    const e = minutes(SLOTS[i][1])
    const top = i * (ROW + GAP)
    if (nowMin >= s && nowMin < e) return top + ((nowMin - s) / (e - s)) * ROW
    const next = i + 1 < SLOTS.length ? minutes(SLOTS[i + 1][0]) : null
    if (next !== null && nowMin >= e && nowMin < next) return top + ROW + ((nowMin - e) / (next - e)) * GAP
  }
  return null
}

/** One week on the cream page: the same label and headline as the days above it, then the grid by slot. */
function WeekPage({
  offset,
  now,
  all,
  title,
  selected,
  onDay,
  onOpen,
  onAdd,
  onEdit,
  onShare,
  arriving,
}: {
  offset: number
  now: Date
  all: Session[]
  title: string
  selected?: Date
  onDay: (d: Date) => void
  onOpen: (s: Session, date: Date) => void
  onAdd: (preset: SlotPreset) => void
  onEdit: (slot: CustomSlot) => void
  onShare: () => void
  arriving: MotionValue<number>
}) {
  // As the week scrolls up into view its headline rises, then the days come up one after another.
  const headY = useTransform(arriving, [0.2, 0.8], [70, 0])
  const headOpacity = useTransform(arriving, [0.2, 0.7], [0, 1])
  const r0 = useTransform(arriving, [0.3, 0.75], [90, 0])
  const r1 = useTransform(arriving, [0.36, 0.8], [90, 0])
  const r2 = useTransform(arriving, [0.42, 0.85], [90, 0])
  const r3 = useTransform(arriving, [0.48, 0.9], [90, 0])
  const r4 = useTransform(arriving, [0.54, 0.95], [90, 0])
  const r5 = useTransform(arriving, [0.6, 1], [90, 0])
  const r6 = useTransform(arriving, [0.64, 1], [90, 0])
  const rise = [r0, r1, r2, r3, r4, r5, r6]
  const start = weekStart(now, offset)
  const sessions = inWeek(all, start)
  const days = weekDays(sessions)
  const today = offset === 0 ? now.getDay() : -1
  const nowMin = nowMinutes(now)
  const cols = `38px repeat(${days.length}, minmax(0, 1fr))`
  const lineY = today >= 0 && days.includes(today) ? nowY(nowMin) : null
  const picked = selected && isoDate(weekStart(selected)) === isoDate(start) ? selected.getDay() : -1
  const types = (['lecture', 'lab', 'tutorial', 'mine'] as ClassType[]).filter((t) => t !== 'mine' || sessions.some((s) => s.mine))

  return (
    <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-4 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
      <motion.header style={{ y: headY, opacity: headOpacity }} className="flex items-start justify-between gap-4 px-2 pt-2">
        <div className="flex min-w-0 flex-1 flex-col gap-0.5">
          <p className="label text-[12px] tracking-[0.14em]">
            {title} · {fmtWeekRange(start, days.length)}
          </p>
          <FitTitle>{HEADLINE[offset] ?? `${title}.`}</FitTitle>
        </div>
        {/* Same ring as the Menu button on the days above. */}
        <button
          type="button"
          onClick={onShare}
          aria-label="Share as a picture"
          className="press tint mt-1 grid size-12 shrink-0 place-items-center rounded-full border-[2.5px] border-navy"
        >
          <Icon name="share" className="size-[18px]" />
        </button>
      </motion.header>

      <div className="mt-6 grid gap-x-[5px]" style={{ gridTemplateColumns: cols }}>
        <div />
        {days.map((d, c) => {
          const date = addDays(start, d)
          return (
            <motion.button
              key={d}
              style={{ y: rise[c] }}
              type="button"
              onClick={() => onDay(date)}
              aria-label={`${DAY_LONG[d]} ${date.getDate()}, show the day`}
              className={cx(
                'press mb-2 flex h-14 flex-col items-center justify-center rounded-[14px]',
                d === today ? 'bg-navy text-cream' : 'tint',
                d === picked && d !== today && 'shadow-[inset_0_0_0_2px_var(--color-navy)]',
              )}
            >
              <span className="label text-[9px]">{DAY_SHORT[d]}</span>
              <span className="display text-[20px] leading-tight">{date.getDate()}</span>
            </motion.button>
          )
        })}
      </div>

      <div className="relative grid gap-x-[5px]" style={{ gridTemplateColumns: cols, gridTemplateRows: `repeat(${SLOTS.length}, ${ROW}px)`, rowGap: GAP }}>
        {SLOTS.map(([s, e], i) => (
          <div key={i} className="label flex flex-col justify-between py-1 text-[9px] leading-none tracking-[0.04em]" style={{ gridColumn: 1, gridRow: i + 1 }}>
            <span>{s}</span>
            <span className="text-muted">{e}</span>
          </div>
        ))}
        {days.map((d, c) => (
          <motion.div key={d} className={cx('rounded-[14px]', d === today ? 'bg-navy/[0.06]' : 'bg-navy/[0.025]')} style={{ gridColumn: c + 2, gridRow: `1 / ${SLOTS.length + 1}`, y: rise[c] }} />
        ))}
        {SLOTS.slice(1).map((_, i) => (
          <div key={i} className="pointer-events-none absolute right-0 left-[43px] h-px bg-navy/10" style={{ top: (i + 1) * (ROW + GAP) - GAP / 2 }} />
        ))}
        {lineY !== null && (
          <div className="pointer-events-none absolute right-0 left-[38px] z-30 h-0.5 rounded-full bg-alert" style={{ top: lineY - 1 }} aria-hidden="true">
            <span className="absolute -top-[3px] -left-1 size-2 rounded-full bg-alert" />
          </div>
        )}
        {/* Empty spots: tap to add your own slot there. */}
        {days.map((d, c) =>
          SLOTS.map(([from, to], slot) =>
            sessions.some((s) => s.meeting.day === d && s.slot <= slot && slot < s.slot + s.span) ? null : (
              <motion.button
                key={`add-${d}-${slot}`}
                type="button"
                aria-label={`Add a slot on ${DAY_LONG[d]} at ${from}`}
                onClick={() => onAdd({ day: d, date: isoDate(addDays(start, d)), start: from, end: to })}
                className="press tint group relative z-10 grid place-items-center rounded-[12px] active:bg-navy/[0.07]"
                style={{ gridColumn: c + 2, gridRow: slot + 1, y: rise[c] }}
              >
                {/* Always shown, faintly: phones have no hover to reveal it. Darker on press, hover or focus. */}
                <svg
                  viewBox="0 0 12 12"
                  aria-hidden="true"
                  className="size-3 stroke-navy/25 transition-colors group-hover:stroke-navy/60 group-focus-visible:stroke-navy/60 group-active:stroke-navy/60"
                  fill="none"
                  strokeWidth={2}
                  strokeLinecap="round"
                >
                  <path d="M6 1.5v9M1.5 6h9" />
                </svg>
              </motion.button>
            ),
          ),
        )}
        {days.map((d, c) =>
          SLOTS.map((_, slot) => {
            const cell = sessions.filter((s) => s.meeting.day === d && s.slot === slot)
            if (cell.length === 0) return null
            return (
              <motion.div key={`${d}-${slot}`} className="relative z-20 flex min-w-0 gap-0.5" style={{ gridColumn: c + 2, gridRow: `${slot + 1} / span ${Math.max(...cell.map((s) => s.span))}`, y: rise[c] }}>
                {cell.map((s, i) => {
                  const live = d === today && s.start <= nowMin && nowMin < s.end
                  return (
                    <button
                      key={i}
                      type="button"
                      onClick={() => (s.mine ? onEdit(s.mine) : onOpen(s, addDays(start, d)))}
                      aria-label={`${s.cls.name}, ${TYPE_NAME[s.type].toLowerCase()}, ${DAY_LONG[d]} ${s.meeting.start} to ${s.meeting.end}${s.meeting.room ? `, room ${s.meeting.room}` : ''}${s.clash ? ', clashes with another class' : ''}${s.cls.changed ? ', changed' : ''}`}
                      className={cx(
                        'press relative flex min-w-0 flex-1 flex-col overflow-hidden rounded-[12px] p-1.5 text-left',
                        BLOCK[s.type],
                        live && 'ring-[2.5px] ring-navy ring-offset-2 ring-offset-cream',
                        s.clash && 'outline-2 -outline-offset-2 outline-alert',
                      )}
                    >
                      <span className={cx('display leading-none tracking-[-0.03em]', cell.length > 1 ? 'text-[12px] break-all' : 'truncate text-[16px]')}>{s.cls.code}</span>
                      <span className="label mt-1 text-[9px] tracking-[0.04em]">
                        {s.meeting.start}
                        {s.span > 1 && `–${s.meeting.end}`}
                      </span>
                      {cell.length === 1 && <span className="mt-auto truncate text-[11px] font-semibold">{s.meeting.room}</span>}
                      {/* Changed since you last looked (dot), and has notes (square), in the block's own text colour. */}
                      {s.cls.changed && <span className="absolute top-1.5 right-1.5 size-1.5 rounded-full bg-current" />}
                      {notesOn(s, addDays(start, d)) > 0 && <span aria-hidden="true" className="absolute right-1.5 bottom-1.5 size-1.5 rounded-[2px] bg-current opacity-60" />}
                    </button>
                  )
                })}
              </motion.div>
            )
          }),
        )}
      </div>

      <div className="mt-8 flex items-center justify-center gap-5 border-t-[1.5px] border-navy/10 pt-5">
        {types.map((t) => (
          <span key={t} className="label flex items-center gap-2 text-[10px]">
            <span className={cx('size-3 rounded-[4px]', BLOCK[t])} />
            {TYPE_NAME[t]}
          </span>
        ))}
      </div>
    </div>
  )
}
