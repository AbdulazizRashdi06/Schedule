import { useCallback, useEffect, useState } from 'react'
import { api } from './api'

export interface NotifySettings {
  /** Minutes before each class; null = no reminders. */
  remindMinutes: number | null
  /** Alert when one of your classes changes. */
  changes: boolean
  /** Devices (browsers, installed apps) with notifications on. */
  devices: number
}

/** Whether this browser can take notifications at all. */
export const pushSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window

/** iPhone and iPad only allow notifications for web apps added to the Home Screen. */
export const needsHomeScreen = () =>
  /iPad|iPhone|iPod/.test(navigator.userAgent) && !(matchMedia('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone)

/** The server's public key, as the browser wants it. */
function key(base64: string) {
  const pad = '='.repeat((4 - (base64.length % 4)) % 4)
  const raw = atob((base64 + pad).replace(/-/g, '+').replace(/_/g, '/'))
  return Uint8Array.from(raw, (c) => c.charCodeAt(0))
}

const registration = () => navigator.serviceWorker.register('/sw.js').then(() => navigator.serviceWorker.ready)

/** Whose notifications: a student's ('/me') or, on the teacher side, a teacher's ('/teach'). */
export type PushBase = '/me' | '/teach'

/**
 * Whether this device gets the signed-in student's (or teacher's) notifications. The browser can keep a subscription the server no
 * longer has for this student (they were removed, their sign-in ran out, an earlier save failed), so it is sent again:
 * the server keeps it once and moves it to whoever is signed in. If that fails, the device counts as off.
 */
export async function deviceSubscribed(base: PushBase = '/me') {
  if (!pushSupported() || Notification.permission !== 'granted') return false
  const sub = await (await registration()).pushManager.getSubscription()
  if (!sub) return false
  try {
    await api.post(`${base}/push`, sub.toJSON())
    return true
  } catch {
    return false
  }
}

export type EnableResult = 'on' | 'denied' | 'unsupported' | 'home-screen'

/** Asks for permission (call straight from a tap) and signs this device up for the student's notifications. */
export async function enableDevice(base: PushBase = '/me'): Promise<EnableResult> {
  if (needsHomeScreen()) return 'home-screen'
  if (!pushSupported()) return 'unsupported'
  // First, before anything awaits: browsers only show the prompt in direct answer to a tap.
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return 'denied'
  const reg = await registration()
  const { publicKey } = await api.get<{ publicKey: string }>('/push/key')
  const existing = await reg.pushManager.getSubscription()
  const sub = existing ?? (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key(publicKey) }))
  try {
    await api.post(`${base}/push`, sub.toJSON())
  } catch (e) {
    // The server didn't take it: don't leave behind a subscription made just now that nothing sends to.
    if (!existing) await sub.unsubscribe().catch(() => {})
    throw e
  }
  return 'on'
}

/** This browser's push subscription, if it has one. */
export async function deviceSubscription() {
  if (!pushSupported()) return null
  // No worker registered means no subscription; don't register one (or wait for it) just to find that out.
  const reg = await navigator.serviceWorker.getRegistration('/')
  return (await reg?.pushManager.getSubscription()) ?? null
}

/** Stops notifications on this device. */
export async function disableDevice(base: PushBase = '/me') {
  const sub = await deviceSubscription()
  if (!sub) return
  await api.post(`${base}/push/remove`, { endpoint: sub.endpoint }).catch(() => {})
  await sub.unsubscribe()
}

/** What each answer means, for the student. */
export const ENABLE_MESSAGE: Record<Exclude<EnableResult, 'on'>, string> = {
  'home-screen': 'On iPhone, add Classy to your Home Screen first (Share, then Add to Home Screen), and turn this on from there.',
  denied: 'Notifications are blocked for Classy. Allow them in your browser’s site settings, then try again.',
  unsupported: 'This browser can’t show notifications. Try Chrome, Edge or Safari.',
}

/**
 * The notification settings, shared by the menu sheet and the session page's reminder button. One value per side: a
 * student's and a teacher's are different settings, and a teaching assistant has both.
 */
const shared: Partial<Record<PushBase, NotifySettings>> = {}
const listeners: Record<PushBase, Set<(s: NotifySettings) => void>> = { '/me': new Set(), '/teach': new Set() }
const publish = (base: PushBase, s: NotifySettings) => {
  shared[base] = s
  listeners[base].forEach((l) => l(s))
}

/** Forgets the signed-out person's settings, so whoever signs in next doesn't see them while theirs load. */
export function resetNotifySettings() {
  delete shared['/me']
  delete shared['/teach']
  generation++
}
/** Goes up on sign-out, so settings that arrive after it aren't kept for the next student. */
let generation = 0

export function useNotifySettings(base: PushBase = '/me') {
  const [settings, setSettings] = useState<NotifySettings | null>(shared[base] ?? null)
  const reload = useCallback(() => {
    const asked = generation
    return api.get<NotifySettings>(`${base}/notifications`).then((s) => {
      if (asked === generation) publish(base, s)
    }, () => {})
  }, [base])
  useEffect(() => {
    const mine = listeners[base]
    mine.add(setSettings)
    void reload()
    return () => {
      mine.delete(setSettings)
    }
  }, [base, reload])
  const save = useCallback(async (next: Pick<NotifySettings, 'remindMinutes' | 'changes'>) => {
    const before = shared[base]
    if (before) publish(base, { ...before, ...next })
    try {
      await api.put(`${base}/notifications`, next)
    } catch (e) {
      if (before) publish(base, before)
      throw e
    }
  }, [base])
  return { settings, save, reload }
}
