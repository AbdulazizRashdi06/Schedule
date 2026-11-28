import { store } from './api'

/** How class times read: "13:15" or "1:15 PM". */
export type TimeFormat = '24' | '12'
/** What home shows first: today's classes, or the week grid below them. */
export type HomeView = 'day' | 'week'
/** Light, dark, or whatever the phone is set to. */
export type Mode = 'light' | 'dark' | 'auto'

const TIME_KEY = 'time-format'
const HOME_KEY = 'home-view'
const MODE_KEY = 'mode'

const pick = <T extends string>(key: string, allowed: readonly T[], fallback: T): T => {
  const v = store.get(key) as T | null
  return v && allowed.includes(v) ? v : fallback
}

/** The student's choices on this device (Menu → Settings). Read at render: changing one happens on the settings page, so every other screen draws fresh with it. */
export const prefs: { timeFormat: TimeFormat; home: HomeView; mode: Mode } = {
  timeFormat: pick(TIME_KEY, ['24', '12'], '24'),
  home: pick(HOME_KEY, ['day', 'week'], 'day'),
  mode: pick(MODE_KEY, ['light', 'dark', 'auto'], 'light'),
}

export function setTimeFormat(f: TimeFormat) {
  prefs.timeFormat = f
  store.set(TIME_KEY, f)
}

export function setHome(h: HomeView) {
  prefs.home = h
  store.set(HOME_KEY, h)
}

const wantsDark = () => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false

/** Whether the page is dark right now. */
export const isDark = () => prefs.mode === 'dark' || (prefs.mode === 'auto' && wantsDark())

/** Colours the browser's own bar like the page, whatever theme, background or mode made it. */
export function syncBar() {
  const page = document.body ? getComputedStyle(document.body).backgroundColor : ''
  if (page) document.querySelector('meta[name="theme-color"]')?.setAttribute('content', page)
}

/** Puts light or dark on the page (index.css: html[data-mode="dark"]). */
export function applyMode() {
  const root = document.documentElement
  if (isDark()) root.dataset.mode = 'dark'
  else delete root.dataset.mode
  syncBar()
}

export function setMode(m: Mode) {
  prefs.mode = m
  store.set(MODE_KEY, m)
  applyMode()
}

/** Call once at start: applies the saved mode, and follows the phone when it's on Auto. */
export function setUpMode() {
  applyMode()
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => {
    if (prefs.mode === 'auto') applyMode()
  })
}
