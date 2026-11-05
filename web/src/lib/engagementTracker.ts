import { API_BASE } from './api'

// Only categories leave the device: never URLs, course IDs, group IDs, or invite codes.
export function screenFor(path: string): string | null {
  if (path === '/') return 'home'
  if (['/schedule', '/setup', '/edit', '/colours', '/groups'].includes(path)) return path.slice(1)
  if (/^\/course\/[^/]+$/.test(path)) return 'course'
  if (/^\/groups\/[^/]+$/.test(path)) return 'group'
  if (/^\/join\/[^/]+$/.test(path)) return 'join'
  return null
}

interface ScreenCounter { screen: string; activeMs: number; views: number }
export interface Runner { navigate: (path: string) => void; stop: () => void }

export function startTracking(path: string, denied: () => void): Runner {
  let sessionId = crypto.randomUUID().replace(/-/g, '')
  let sequence = 0
  let screens = new Map<string, ScreenCounter>()
  let current = screenFor(path)
  let previous = performance.now()
  let lastInput = previous
  let foreground = document.visibilityState === 'visible' && document.hasFocus()
  let changed = false
  let stopped = false
  let sending = false

  const counter = () => {
    if (!current) return null
    let value = screens.get(current)
    if (!value) { value = { screen: current, activeMs: 0, views: 0 }; screens.set(current, value) }
    return value
  }
  const view = () => { const value = counter(); if (value) { value.views++; changed = true } }
  const tick = () => {
    const now = performance.now()
    // Long gaps can be device sleep or a suspended WebView; never count them as reading.
    const elapsed = Math.max(0, Math.min(now, lastInput + 60_000) - previous)
    const value = counter()
    if (foreground && now - previous <= 30_000 && value && elapsed > 0) {
      value.activeMs += Math.floor(elapsed)
      changed = true
    }
    previous = now
  }
  const reset = () => {
    sessionId = crypto.randomUUID().replace(/-/g, '')
    sequence = 0
    screens = new Map()
    view()
  }
  const send = (keepalive = false) => {
    if (stopped || !changed || (sending && !keepalive)) return
    const sentId = sessionId
    const payload = { sessionId, sequence: ++sequence, screens: [...screens.values()].map(x => ({ ...x })) }
    changed = false
    sending = true
    void fetch(API_BASE + '/api/me/analytics/pulse', {
      method: 'POST', credentials: 'same-origin', keepalive,
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
    }).then(response => {
      if (stopped || sentId !== sessionId) return
      if (response.status === 403 || response.status === 401) { stopped = true; denied() }
      else if (response.status === 409) { reset() }
      else if (!response.ok) changed = true
    }).catch(() => {
      // Retry cumulative counters; the server bounds credited time after a delivery gap.
      if (!stopped && sentId === sessionId) changed = true
    }).finally(() => { sending = false })
  }
  const interact = () => {
    tick()
    const now = performance.now()
    if (now - lastInput >= 30 * 60_000) reset()
    lastInput = now
  }
  const visibility = () => {
    tick()
    foreground = document.visibilityState === 'visible' && document.hasFocus()
    if (foreground) interact()
    else send(true)
  }
  const leave = () => { tick(); foreground = false; send(true) }
  const inputs = ['pointerdown', 'keydown', 'scroll', 'touchstart'] as const
  inputs.forEach(type => window.addEventListener(type, interact, { passive: true, capture: true }))
  document.addEventListener('visibilitychange', visibility)
  window.addEventListener('focus', visibility)
  window.addEventListener('blur', visibility)
  window.addEventListener('pagehide', leave)
  window.addEventListener('pageshow', visibility)
  const ticking = window.setInterval(tick, 5000)
  const heartbeat = window.setInterval(() => { tick(); send() }, 15000)
  view()
  send()
  return {
    navigate(next) {
      tick()
      const category = screenFor(next)
      if (category === current) return
      send()
      current = category
      view()
      send()
    },
    stop() {
      // Cleanup also runs on consent withdrawal. Do not send another sample here.
      stopped = true
      window.clearInterval(ticking)
      window.clearInterval(heartbeat)
      inputs.forEach(type => window.removeEventListener(type, interact, { capture: true }))
      document.removeEventListener('visibilitychange', visibility)
      window.removeEventListener('focus', visibility)
      window.removeEventListener('blur', visibility)
      window.removeEventListener('pagehide', leave)
      window.removeEventListener('pageshow', visibility)
    },
  }
}
