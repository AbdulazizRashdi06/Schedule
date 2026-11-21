// Classy Sync: fetches every course's sections from EduWave (one a second) and sends them to Classy,
// where staff check and apply them from Upload. Progress is saved after each course, so a sync picks up
// where it stopped after a sign-out or a browser restart.
const CLASSY = 'https://schedule.tryzarqa.com'
const PLAN_TABS = 'https://eduwave.gutech.edu.om/*AdvisorStudentPlan.aspx*'
const GAP_MS = 1000
const MAX_RUN_MS = 12 * 60 * 60 * 1000 // an unfinished sync older than this starts again, so old rows aren't sent as new

const get = (keys) => chrome.storage.local.get(keys)
const set = (o) => chrome.storage.local.set(o)
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let looping = false
let stopping = false
const stale = (run) => run && !looping && Date.now() - (run.startedAt || 0) > MAX_RUN_MS
const same = (a, b) => !!a && !!b && a.startedAt === b.startedAt

function badge(text, colour = '#0b1957') {
  chrome.action.setBadgeText({ text })
  chrome.action.setBadgeBackgroundColor({ color: colour })
}

async function classy(path, init = {}) {
  const { token } = await get('token')
  if (!token) throw Object.assign(new Error('Connect to Classy first.'), { status: 401 })
  const res = await fetch(CLASSY + '/api' + path, {
    ...init,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, ...(init.headers || {}) },
  })
  const data = await res.json().catch(() => ({}))
  // The token was disconnected in Classy: forget it, so the popup offers Connect again.
  if (res.status === 401) await chrome.storage.local.remove('token')
  if (!res.ok) throw Object.assign(new Error(data.error || `Classy answered ${res.status}.`), { status: res.status })
  return data
}

// ---- Fetching one course: in an open Advisor Plan tab if there is one, otherwise in the hidden page ----

async function ensureOffscreen() {
  if (await chrome.offscreen.hasDocument()) return
  await chrome.offscreen.createDocument({ url: 'offscreen.html', reasons: ['DOM_PARSER'], justification: 'Read EduWave section tables.' })
}

async function fetchCourse(form, code) {
  const msg = { url: form.url, fields: form.fields, code }
  const tabs = await chrome.tabs.query({ url: PLAN_TABS })
  for (const tab of tabs) {
    try {
      const r = await chrome.tabs.sendMessage(tab.id, { type: 'tab-fetch', ...msg })
      if (r) return r
    } catch {
      /* the tab moved on or is still loading: try the next way */
    }
  }
  await ensureOffscreen()
  return chrome.runtime.sendMessage({ type: 'offscreen-fetch', ...msg })
}

// ---- A sync ----

/** Starts a sync, or carries on the stored one. With `replacing`, it only starts again in place of that old run,
 * so a Stop pressed meanwhile isn't undone by a new sync. */
async function start(replacing) {
  const { token, form, ...saved } = await get(['token', 'form', 'run'])
  if (replacing && !same(saved.run, replacing)) return
  if (!token) throw new Error('Connect to Classy first.')
  if (!form) throw new Error('Open Advisor Plan in EduWave (through its menu) first.')
  const run = stale(saved.run) ? null : saved.run
  if (run && (run.status === 'running' || run.status === 'paused')) return resume()
  const list = await classy('/sync/courses')
  if (replacing && !same((await get('run')).run, replacing)) return
  await set({
    run: {
      status: 'running',
      semesterId: list.semester.id,
      semester: list.semester.name,
      codes: list.courses.map((c) => c.eduwave),
      index: 0,
      rows: [],
      failed: [],
      startedAt: Date.now(),
      message: '',
    },
  })
  loop()
}

async function resume() {
  const { run } = await get('run')
  if (!run) return
  run.status = 'running'
  run.message = ''
  await set({ run })
  loop()
}

async function loop() {
  if (looping) return
  looping = true
  try {
    let { run, form } = await get(['run', 'form'])
    while (run && run.status === 'running' && run.index < run.codes.length) {
      badge(String(Math.floor((run.index / run.codes.length) * 100)))
      const code = run.codes[run.index]
      let r = await fetchCourse(form, code).catch((e) => ({ ok: false, error: e.message }))
      if (!r?.ok && r?.error === 'bad-page') {
        await sleep(3000)
        r = await fetchCourse(form, code).catch((e) => ({ ok: false, error: e.message }))
      }
      if (r?.ok) {
        run.rows.push(...r.rows)
        if (r.fields?.length) form = { ...form, fields: r.fields, at: Date.now() }
      } else if (r?.error === 'signed-out') {
        run.status = 'paused'
        run.message = 'EduWave signed you out. Sign in and open Advisor Plan; the sync carries on from where it stopped.'
        await set({ run, form })
        badge('!', '#b3261e')
        return
      } else run.failed.push(code)
      run.index++
      if (stopping) return
      await set({ run, form })
      await sleep(GAP_MS)
      if (stopping) return
    }
    if (run && run.status === 'running') await finish(run)
  } finally {
    // A new sync started while a stopped one was finishing its last course: run it now.
    const restart = stopping
    looping = false
    stopping = false
    if (restart) get('run').then(({ run }) => run?.status === 'running' && loop())
  }
}

async function finish(run) {
  badge('↑')
  // Stop (and then maybe Sync now) can change the stored run during the upload: only touch it if it is still this one.
  const mine = async () => same((await get('run')).run, run)
  try {
    const result = await classy('/sync/timetable', {
      method: 'POST',
      body: JSON.stringify({ semesterId: run.semesterId, rows: run.rows, failed: run.failed }),
    })
    const last = { at: Date.now(), ok: true, result, failed: run.failed.length }
    if (!(await mine())) return set({ last })
    await set({ run: null, last })
    badge('')
  } catch (e) {
    const ours = await mine()
    if (e.status === 401) {
      if (!ours) return // stopped: don't bring it back
      // Keep the classes fetched so far: after Connect, Carry on sends them again.
      run.status = 'paused'
      run.message = 'Classy didn’t accept this browser’s connection. Press Connect to Classy, then Carry on.'
      await set({ run })
    } else {
      const last = { at: Date.now(), ok: false, error: e.message }
      if (!ours) return set({ last })
      await set({ run: null, last })
    }
    badge('!', '#b3261e')
  }
}

// ---- Messages from the popup and the EduWave tab ----

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type === 'form') {
    set({ form: { url: msg.url, fields: msg.fields, at: Date.now() } }).then(async () => {
      const { run } = await get('run')
      if (run?.status === 'paused') start().catch(() => {}) // carries on, or starts again if it is too old
    })
  } else if (msg.type === 'connect') {
    connect().then(
      () => reply({ ok: true }),
      (e) => reply({ ok: false, error: e.message }),
    )
    return true
  } else if (msg.type === 'sync') {
    start().then(
      () => reply({ ok: true }),
      (e) => reply({ ok: false, error: e.message }),
    )
    return true
  } else if (msg.type === 'stop') {
    if (looping) stopping = true
    set({ run: null }).then(() => {
      badge('')
      reply({ ok: true })
    })
    return true
  }
})

/** Makes a sync token with your Classy owner session (sign in to the dashboard in this browser first). */
async function connect() {
  const res = await fetch(CLASSY + '/api/admin/sync-tokens', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: `Classy Sync (${new Date().toLocaleDateString('en-GB')})` }),
  })
  if (res.status === 401 || res.status === 403) throw new Error('Sign in to the Classy dashboard as an owner in this browser, then press Connect again.')
  const data = await res.json().catch(() => ({}))
  if (!res.ok || !data.token) throw new Error(data.error || 'Classy didn’t answer. Try again.')
  await set({ token: data.token })
}

// ---- Every morning at 06:30, and carrying on after the browser restarts ----

function nextMorning() {
  const d = new Date()
  d.setHours(6, 30, 0, 0)
  if (d <= new Date()) d.setDate(d.getDate() + 1)
  return d.getTime()
}

chrome.runtime.onInstalled.addListener(async () => {
  const { auto } = await get('auto')
  if (auto === undefined) await set({ auto: true })
  chrome.alarms.create('daily', { when: nextMorning(), periodInMinutes: 24 * 60 })
})

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name !== 'daily') return
  const { auto } = await get('auto')
  if (auto) start().catch(async (e) => set({ last: { at: Date.now(), ok: false, error: e.message } }))
})

/** Carries on with a sync that was running when the browser closed, or starts a fresh one if it is too old. Once per wake-up. */
let carried = null
function carryOn() {
  carried ??= get('run').then(({ run }) => {
    if (run?.status !== 'running') return
    if (!stale(run)) return loop()
    start(run).catch(async (e) => {
      if (same((await get('run')).run, run)) await set({ run: null, last: { at: Date.now(), ok: false, error: e.message } })
    })
  })
  return carried
}

chrome.runtime.onStartup.addListener(carryOn) // Chrome doesn't wake this worker at launch without it
carryOn()
