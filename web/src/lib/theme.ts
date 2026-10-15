import { store } from './api'

/** A colour theme: the main colour (replaces navy) and the accent (replaces sky). */
export interface Preset {
  id: string
  name: string
  main: string
  accent: string
}

/** Each preset is tuned in index.css (html[data-accent=…]) so text stays readable. */
export const PRESETS: Preset[] = [
  { id: 'navy', name: 'Navy', main: '#0b1957', accent: '#9eccfa' },
  { id: 'forest', name: 'Forest', main: '#103b2c', accent: '#9fdcb8' },
  { id: 'plum', name: 'Plum', main: '#3a1350', accent: '#d3a8f0' },
  { id: 'maroon', name: 'Maroon', main: '#5a1020', accent: '#f4a7b2' },
  { id: 'teal', name: 'Teal', main: '#0b4650', accent: '#8fd8df' },
  { id: 'charcoal', name: 'Charcoal', main: '#1f2328', accent: '#b8c4d4' },
]

export type Accent = { preset: string } | { main: string; accent: string }

const KEY = 'accent'
const VARS = ['--color-navy', '--color-navy-700', '--color-navy-800', '--color-navy-line', '--color-navy-line-2', '--color-sky', '--color-ice', '--color-muted']

export function loadAccent(): Accent {
  try {
    const a = JSON.parse(store.get(KEY) ?? 'null') as Accent | null
    return a ?? { preset: 'navy' }
  } catch {
    return { preset: 'navy' }
  }
}

/** Applies a theme to the page and remembers it on this device. Custom colours derive the in-between shades with color-mix. */
export function applyAccent(a: Accent, save = false) {
  const root = document.documentElement
  for (const v of VARS) root.style.removeProperty(v)
  delete root.dataset.accent
  if ('preset' in a) {
    if (a.preset !== 'navy') root.dataset.accent = a.preset
  } else {
    const mix = (c: string, pct: number, with_ = 'white') => `color-mix(in srgb, ${c} ${pct}%, ${with_})`
    const set = (k: string, v: string) => root.style.setProperty(k, v)
    set('--color-navy', a.main)
    set('--color-navy-700', mix(a.main, 85))
    set('--color-navy-800', mix(a.main, 92))
    set('--color-navy-line', mix(a.main, 75))
    set('--color-navy-line-2', mix(a.main, 65))
    set('--color-sky', a.accent)
    set('--color-ice', mix(a.accent, 40))
    set('--color-muted', mix(a.main, 75, '#808080'))
  }
  if (save) store.set(KEY, JSON.stringify(a))
}

/** WCAG contrast ratio between two "#rrggbb" colours (1 to 21). */
export function contrast(a: string, b: string) {
  const lum = (hex: string) => {
    const [r, g, bl] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
    return 0.2126 * r + 0.7152 * g + 0.0722 * bl
  }
  const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p)
  return (x + 0.05) / (y + 0.05)
}

export const CREAM = '#f8f3ea'

/** Page backgrounds: light ones, so dark text stays readable. The first is the default cream. */
export const BACKGROUNDS = ['#f8f3ea', '#ffffff', '#f1efea', '#eef2f7', '#f3ead8', '#f7ecef', '#eaf3ee']
const BG_KEY = 'background'

export const loadBackground = (): string | null => store.get(BG_KEY)

/** Swaps the page background (cream) and the card colour (paper, a touch lighter). Null = the default cream. */
export function applyBackground(bg: string | null, save = false) {
  const root = document.documentElement
  if (!bg || bg === CREAM) {
    root.style.removeProperty('--color-cream')
    root.style.removeProperty('--color-paper')
  } else {
    root.style.setProperty('--color-cream', bg)
    root.style.setProperty('--color-paper', `color-mix(in srgb, ${bg} 55%, white)`)
  }
  if (save) {
    if (!bg || bg === CREAM) store.remove(BG_KEY)
    else store.set(BG_KEY, bg)
  }
}

/** The main colour of a theme, for contrast checks. */
export const mainOf = (a: Accent) => ('preset' in a ? (PRESETS.find((p) => p.id === a.preset) ?? PRESETS[0]).main : a.main)
