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

/** This device's subscription, if it has one. */
export async function deviceSubscribed() {
  if (!pushSupported() || Notification.permission !== 'granted') return false
  return Boolean(await (await registration()).pushManager.getSubscription())
}

export type EnableResult = 'on' | 'denied' | 'unsupported' | 'home-screen'

/** Asks for permission (call straight from a tap) and signs this device up for the student's notifications. */
export async function enableDevice(): Promise<EnableResult> {
  if (needsHomeScreen()) return 'home-screen'
  if (!pushSupported()) return 'unsupported'
  // First, before anything awaits: browsers only show the prompt in direct answer to a tap.
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return 'denied'
  const reg = await registration()
  const { publicKey } = await api.get<{ publicKey: string }>('/push/key')
  const sub = (await reg.pushManager.getSubscription()) ?? (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key(publicKey) }))
  await api.post('/me/push', sub.toJSON())
  return 'on'
}

/** Stops notifications on this device. */
export async function disableDevice() {
  if (!pushSupported()) return
  const sub = await (await registration()).pushManager.getSubscription()
  if (!sub) return
  await api.post('/me/push/remove', { endpoint: sub.endpoint }).catch(() => {})
  await sub.unsubscribe()
}

/** What each answer means, for the student. */
export const ENABLE_MESSAGE: Record<Exclude<EnableResult, 'on'>, string> = {
  'home-screen': 'On iPhone, add Classy to your Home Screen first (Share, then Add to Home Screen), and turn this on from there.',
  denied: 'Notifications are blocked for Classy. Allow them in your browser’s site settings, then try again.',
  unsupported: 'This browser can’t show notifications. Try Chrome, Edge or Safari.',
}

/** The student's notification settings, shared by the menu sheet and the session page's reminder button. */
let shared: NotifySettings | null = null
const listeners = new Set<(s: NotifySettings) => void>()
const publish = (s: NotifySettings) => {
  shared = s
  listeners.forEach((l) => l(s))
}

export function useNotifySettings() {
  const [settings, setSettings] = useState<NotifySettings | null>(shared)
  const reload = useCallback(() => api.get<NotifySettings>('/me/notifications').then(publish, () => {}), [])
  useEffect(() => {
    listeners.add(setSettings)
    void reload()
    return () => {
      listeners.delete(setSettings)
    }
  }, [reload])
  const save = useCallback(async (next: Pick<NotifySettings, 'remindMinutes' | 'changes'>) => {
    const before = shared
    if (before) publish({ ...before, ...next })
    try {
      await api.put('/me/notifications', next)
    } catch (e) {
      if (before) publish(before)
      throw e
    }
  }, [])
  return { settings, save, reload }
}
