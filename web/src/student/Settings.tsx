import { useState } from 'react'
import { Link } from 'react-router-dom'
import { prefs, setHome, setMode, setTimeFormat, type HomeView, type Mode, type TimeFormat } from '../lib/prefs'
import { useHome, useViewer } from '../lib/viewer'
import { cx } from '../components/ui'
import ChangeYear from './ChangeYear'
import NotificationsSheet from './NotificationsSheet'
import { BackButton, useBack } from './timetable'

interface Option<T> {
  value: T
  name: string
  /** A word under the name, in small type. */
  hint?: string
}

/** One setting: a few tiles, the chosen one filled. */
function Choice<T extends string>({ label, note, value, options, onChange }: { label: string; note?: string; value: T; options: Option<T>[]; onChange: (v: T) => void }) {
  return (
    <section className="flex flex-col gap-2.5">
      <h2 className="label text-[11px] tracking-[0.12em]">{label}</h2>
      <div role="radiogroup" aria-label={label} className={cx('grid gap-2', options.length === 3 ? 'grid-cols-3' : 'grid-cols-2')}>
        {options.map((o) => {
          const on = o.value === value
          return (
            <button
              key={o.value}
              type="button"
              role="radio"
              aria-checked={on}
              onClick={() => onChange(o.value)}
              className={cx(
                'press flex flex-col justify-center gap-1.5 rounded-[16px] px-4 text-left',
                o.hint ? 'h-[68px]' : 'h-14',
                on ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_color-mix(in_srgb,var(--color-navy)_16%,transparent)]',
              )}
            >
              <span className="text-[19px] leading-none font-extrabold tabular-nums">{o.name}</span>
              {o.hint && <span className={cx('label text-[10px] leading-none tracking-[0.1em]', on ? 'text-ice' : 'text-muted')}>{o.hint}</span>}
            </button>
          )
        })}
      </div>
      {note && <p className="text-[14px] font-semibold text-muted">{note}</p>}
    </section>
  )
}

const TIMES: Option<TimeFormat>[] = [
  { value: '24', name: '13:15', hint: '24-hour' },
  { value: '12', name: '1:15 PM', hint: '12-hour' },
]
const HOMES: Option<HomeView>[] = [
  { value: 'day', name: 'Today', hint: 'Your day first' },
  { value: 'week', name: 'The week', hint: 'The grid first' },
]
const MODES: Option<Mode>[] = [
  { value: 'light', name: 'Light' },
  { value: 'dark', name: 'Dark' },
  { value: 'auto', name: 'Auto' },
]

/** Settings: how times read, what home opens on, light or dark, and the way to colours, notifications and your year. Saved on this device. */
export default function Settings() {
  const viewer = useViewer()
  const back = useBack(viewer.home)
  const { me, teach } = useHome()
  const student = viewer.mode === 'student'
  const [time, setTime] = useState(prefs.timeFormat)
  const [home, setHomeView] = useState(prefs.home)
  const [mode, setModeChoice] = useState(prefs.mode)
  const [notify, setNotify] = useState(false)
  const [changingYear, setChangingYear] = useState(false)
  // Reminders belong to a person: a student who has set up, or a teacher signed in as themselves.
  const canNotify = student ? !!me?.setupDone : !!teach?.personal
  const row = 'tint flex h-14 items-center justify-between border-b-[1.5px] border-navy/10 text-left text-[18px] font-extrabold'
  const arrow = (
    <span aria-hidden="true" className="text-[18px]">
      →
    </span>
  )

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex max-w-[480px] flex-col gap-7 px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-10">
        <BackButton onClick={back} />
        <div className="flex flex-col gap-2">
          <h1 className="display text-[56px] leading-[0.86] tracking-[-0.05em]">Settings.</h1>
          <p className="text-[16px] font-semibold text-muted">Saved on this device.</p>
        </div>

        <Choice
          label="Time format"
          value={time}
          options={TIMES}
          onChange={(v) => {
            setTime(v)
            setTimeFormat(v)
          }}
        />
        <Choice
          label="Home opens on"
          value={home}
          options={HOMES}
          onChange={(v) => {
            setHomeView(v)
            setHome(v)
          }}
        />
        <Choice
          label="Appearance"
          note={mode === 'auto' ? 'Light or dark, whichever your device is set to.' : undefined}
          value={mode}
          options={MODES}
          onChange={(v) => {
            setModeChoice(v)
            setMode(v)
          }}
        />

        <nav aria-label="More settings" className="flex flex-col border-t-[1.5px] border-navy/10">
          <Link to={student ? '/colours' : '/teach/colours'} className={row}>
            <span className="flex items-center gap-2.5">
              <span aria-hidden="true" className="flex overflow-hidden rounded-full">
                <span className="size-5 bg-navy" />
                <span className="size-5 bg-sky" />
              </span>
              Colours
            </span>
            {arrow}
          </Link>
          {canNotify && (
            <button type="button" onClick={() => setNotify(true)} className={row}>
              Notifications{arrow}
            </button>
          )}
          {student && me?.setupDone && (
            <button type="button" onClick={() => setChangingYear(true)} className={row}>
              Change my year{arrow}
            </button>
          )}
        </nav>
      </div>
      {canNotify && <NotificationsSheet open={notify} onClose={() => setNotify(false)} />}
      {student && me && <ChangeYear key={String(changingYear)} open={changingYear} onClose={() => setChangingYear(false)} current={me.year} />}
    </div>
  )
}
