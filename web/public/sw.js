/*
 * Classy's service worker.
 * - Offline: the app itself (page, scripts, styles, fonts, icons) is kept, so the installed app opens without a
 *   connection and shows the timetable it saved last time. The API is never cached here: the app keeps its own copy.
 * - Notifications: shows class reminders and change alerts, and opens the right page when one is tapped.
 */
const SHELL = 'classy-shell-v2'
const FILES = 'classy-files-v2'
const KEEP = [SHELL, FILES]
/** Old builds' scripts pile up in the cache; only this many files are kept. */
const MAX_FILES = 80

/** Only the app page itself is kept as the page: not a file (the manifest, an icon) that was opened on its own. */
const isPage = (response) => (response.headers.get('content-type') || '').includes('text/html')

/**
 * The app page and the build's script and stylesheet it loads. The first visit loads those before this worker exists,
 * so without this the installed app couldn't open offline until a second visit. 'reload': straight from the server,
 * not an old copy in the browser's HTTP cache.
 */
async function saveApp() {
  const res = await fetch(new Request('/', { cache: 'reload' }))
  if (!res.ok || !isPage(res)) throw new Error(`The app page answered ${res.status}`)
  const html = await res.clone().text()
  const files = [...new Set([...html.matchAll(/(?:src|href)="(\/assets\/[^"]+)"/g)].map((m) => m[1]))]
  await (await caches.open(FILES)).addAll(files)
  const shell = await caches.open(SHELL)
  await shell.put('/', res)
  await shell.addAll(['/manifest.json', '/icons/icon-192.png'])
}

self.addEventListener('install', (event) => {
  event.waitUntil(saveApp().then(() => self.skipWaiting()))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((names) => Promise.all(names.filter((n) => !KEEP.includes(n)).map((n) => caches.delete(n))))
      .then(() => self.clients.claim()),
  )
})

async function trim() {
  const cache = await caches.open(FILES)
  const keys = await cache.keys()
  // Oldest first: cache keys come back in the order they were added.
  await Promise.all(keys.slice(0, Math.max(0, keys.length - MAX_FILES)).map((k) => cache.delete(k)))
}

/**
 * The page: always the newest when online (so a deploy shows at once), the saved one offline or while the server is
 * down (a 5xx while it restarts). Every route is the same page.
 */
async function page(request) {
  try {
    // 'no-cache': always ask the server (a quick "not changed" when it hasn't), never a copy the browser kept.
    const fresh = await fetch(request, { cache: 'no-cache' })
    if (fresh.ok && isPage(fresh)) (await caches.open(SHELL)).put('/', fresh.clone())
    if (fresh.status >= 500) return (await caches.match('/')) ?? fresh
    return fresh
  } catch {
    return (await caches.match('/')) ?? Response.error()
  }
}

/** Build files have the content's hash in their name and never change: the saved copy is always right. */
async function fixed(request) {
  const hit = await caches.match(request)
  if (hit) return hit
  const fresh = await fetch(request)
  if (fresh.ok) {
    await (await caches.open(FILES)).put(request, fresh.clone())
    void trim()
  }
  return fresh
}

/** Icons, the logo, fonts: the saved copy at once, refreshed in the background for next time. */
async function refreshed(request) {
  const cache = await caches.open(FILES)
  const hit = await cache.match(request)
  const update = fetch(request)
    .then((fresh) => {
      if (fresh.ok || fresh.type === 'opaque') void cache.put(request, fresh.clone()).then(trim)
      return fresh
    })
    .catch(() => hit ?? Response.error())
  return hit ?? update
}

self.addEventListener('fetch', (event) => {
  const { request } = event
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin === self.location.origin) {
    if (url.pathname.startsWith('/api/')) return
    if (request.mode === 'navigate') return event.respondWith(page(request))
    if (url.pathname.startsWith('/assets/')) return event.respondWith(fixed(request))
    return event.respondWith(refreshed(request))
  }
  if (url.hostname === 'fonts.googleapis.com' || url.hostname === 'fonts.gstatic.com') event.respondWith(refreshed(request))
})

self.addEventListener('push', (event) => {
  let data = {}
  try {
    data = event.data ? event.data.json() : {}
  } catch {
    data = { title: 'Classy', body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(data.title || 'Classy', {
      body: data.body || '',
      icon: '/icons/icon-192.png',
      badge: '/icons/badge-96.png',
      // The same class's reminder replaces itself instead of stacking.
      tag: data.tag || undefined,
      data: { url: data.url || '/' },
    }),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const target = new URL(event.notification.data?.url || '/', self.location.origin).href
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((open) => {
      // Reuse an open Classy window when there is one.
      const win = open.find((c) => new URL(c.url).origin === self.location.origin)
      if (!win) return self.clients.openWindow(target)
      // navigate() only works on a window this worker controls (not one opened before it took over): open a new one then.
      return win
        .navigate(target)
        .then((c) => (c ?? win).focus())
        .catch(() => self.clients.openWindow(target))
    }),
  )
})
