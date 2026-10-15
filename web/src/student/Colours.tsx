import { useState } from 'react'
import { BACKGROUNDS, CREAM, PRESETS, applyAccent, applyBackground, contrast, loadAccent, loadBackground, mainOf, type Accent } from '../lib/theme'
import { cx } from '../components/ui'
import { Icon, RoundButton, useBack } from './timetable'

/** Pick the app's colours: a designed preset, or your own main and accent colours. Saved on this device. */
export default function Colours() {
  const back = useBack('/')
  const [accent, setAccent] = useState<Accent>(loadAccent)
  const custom = 'main' in accent ? accent : null
  const [main, setMain] = useState(custom?.main ?? '#0b1957')
  const [second, setSecond] = useState(custom?.accent ?? '#9eccfa')

  const [bg, setBg] = useState(() => loadBackground() ?? CREAM)
  const choose = (a: Accent) => {
    setAccent(a)
    applyAccent(a, true)
  }
  const chooseBg = (b: string) => {
    setBg(b)
    applyBackground(b, true)
  }
  const ratio = contrast(main, bg)
  const bgRatio = contrast(mainOf(accent), bg)

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex max-w-[480px] flex-col gap-6 px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-10">
        <RoundButton label="Back" onClick={back} className="border-[2.5px] border-navy">
          <Icon name="back" />
        </RoundButton>
        <div className="flex flex-col gap-2">
          <h1 className="display text-[56px] leading-[0.86] tracking-[-0.05em]">Colours.</h1>
          <p className="text-[16px] font-semibold text-muted">Pick your colours and background. They’re saved on this device.</p>
        </div>

        <div role="radiogroup" aria-label="Colour themes" className="grid grid-cols-2 gap-2.5">
          {PRESETS.map((p) => {
            const on = 'preset' in accent && accent.preset === p.id
            return (
              <button
                key={p.id}
                type="button"
                role="radio"
                aria-checked={on}
                aria-label={p.name}
                onClick={() => choose({ preset: p.id })}
                className={cx('press relative rounded-[20px] p-2.5', on ? 'shadow-[inset_0_0_0_3px_var(--color-navy)]' : 'shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]')}
              >
                <span className="flex h-16 overflow-hidden rounded-[13px]">
                  <span className="flex-[2]" style={{ background: p.main }} />
                  <span className="flex-1" style={{ background: p.accent }} />
                </span>
                {on && (
                  <span className="absolute top-4 left-4 grid size-6 place-items-center rounded-full bg-cream text-navy">
                    <Icon name="check" className="size-3.5" strokeWidth={3} />
                  </span>
                )}
              </button>
            )
          })}
        </div>

        <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
          <h2 className="text-[20px] font-extrabold">Background</h2>
          <div role="radiogroup" aria-label="Background" className="flex flex-wrap items-center gap-2">
            {BACKGROUNDS.map((b, i) => (
              <button
                key={b}
                type="button"
                role="radio"
                aria-checked={bg === b}
                aria-label={i === 0 ? 'Default background' : `Background ${b}`}
                onClick={() => chooseBg(b)}
                className={cx('press grid size-11 place-items-center rounded-full shadow-[inset_0_0_0_2px_rgb(0_0_0/0.12)]', bg === b && 'ring-[3px] ring-navy ring-offset-2 ring-offset-cream')}
                style={{ background: b }}
              >
                {bg === b && <Icon name="check" className="size-4 text-navy" strokeWidth={3} />}
              </button>
            ))}
            <label className="flex items-center gap-2 rounded-full bg-paper py-1 pr-3 pl-1 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.08)]">
              <input type="color" aria-label="Any background colour" value={bg} onChange={(e) => chooseBg(e.target.value)} className="size-9 cursor-pointer rounded-full border-0 bg-transparent p-0" />
              <span className="font-mono text-[12px] font-bold">{bg}</span>
            </label>
          </div>
          {bgRatio < 4.5 && (
            <p className="text-[13px] font-semibold text-alert" role="status">
              Text may be hard to read on this background with your colours. A lighter background works better.
            </p>
          )}
        </section>

        <section className={cx('flex flex-col gap-3.5 rounded-[20px] p-4', custom ? 'shadow-[inset_0_0_0_3px_var(--color-navy)]' : 'shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]')}>
          <div className="flex items-center justify-between">
            <h2 className="text-[20px] font-extrabold">Your own colours</h2>
            {custom && <Icon name="check" className="size-4" />}
          </div>
          <div className="grid grid-cols-2 gap-2.5">
            {[
              { label: 'Main', value: main, set: setMain },
              { label: 'Accent', value: second, set: setSecond },
            ].map((c) => (
              <label key={c.label} className="flex items-center gap-2.5 rounded-[14px] bg-paper p-2.5">
                <input type="color" value={c.value} onChange={(e) => c.set(e.target.value)} className="size-10 cursor-pointer rounded-[10px] border-0 bg-transparent p-0" />
                <span className="flex flex-col">
                  <span className="label text-[10px] tracking-[0.12em]">{c.label}</span>
                  <span className="font-mono text-[13px] font-bold">{c.value}</span>
                </span>
              </label>
            ))}
          </div>
          {/* Preview with the picked colours. */}
          <div className="flex items-center gap-2.5 rounded-[16px] p-3" style={{ background: main, color: CREAM }}>
            <span className="rounded-[10px] px-2.5 py-2 text-[13px] font-extrabold" style={{ background: second, color: main }}>
              CS 1 101
            </span>
            <span className="text-[14px] font-bold">Programming · 08:00</span>
          </div>
          {ratio < 4.5 && (
            <p className="text-[13px] font-semibold text-alert" role="status">
              That main colour is light for your background, so text may be hard to read. A darker one works better.
            </p>
          )}
          <button type="button" onClick={() => choose({ main, accent: second })} className="press h-12 rounded-full bg-navy text-[16px] font-extrabold text-cream">
            Use these colours
          </button>
        </section>
      </div>
    </div>
  )
}
