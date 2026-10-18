// Classy Sync: fetches every course's sections from EduWave (one a second) and sends them to Classy,
// where staff check and apply them from Upload. Progress is saved after each course, so a sync picks up
// where it stopped after a sign-out or a browser restart.
const CLASSY = 'https://schedule.tryzarqa.com'
const PLAN_TABS = 'https://eduwave.gutech.edu.om/*AdvisorStudentPlan.aspx*'
const GAP_MS = 1000

const get = (keys) => chrome.storage.local.get(keys)
const set = (o) => chrome.storage.local.set(o)
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
let looping = false
let stopping = false

function badge(text, colour = '#0b1957') {
  chrome.action.setBadgeText({ text })
  chrome.action.setBadgeBackgroundColor({ color: colour })
}

async function classy(path, init = {}) {
  const { token } = await get('token')
  const res = await fetch(CLASSY + '/api' + path, {
    ...init,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, ...(init.headers || {}) },
  })
  const data = await res.json().catch(() => ({}))
  if (!res.ok) throw new Error(data.error || `Classy answered ${res.status}.`)
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

async function start() {
  const { token, form, run } = await get(['token', 'form', 'run'])
  if (!token) throw new Error('Connect to Classy first.')
  if (!form) throw new Error('Open Advisor Plan in EduWave (through its menu) first.')
  if (run && (run.status === 'running' || run.status === 'paused')) return resume()
  const list = await classy('/sync/courses')
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
    looping = false
    stopping = false
  }
}

async function finish(run) {
  badge('↑')
  try {
    const result = await classy('/sync/timetable', {
      method: 'POST',
      body: JSON.stringify({ semesterId: run.semesterId, rows: run.rows, failed: run.failed }),
    })
    await set({ run: null, last: { at: Date.now(), ok: true, result, failed: run.failed.length } })
    badge('')
  } catch (e) {
    await set({ run: null, last: { at: Date.now(), ok: false, error: e.message } })
    badge('!', '#b3261e')
  }
}

// ---- Messages from the popup and the EduWave tab ----

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type === 'form') {
    set({ form: { url: msg.url, fields: msg.fields, at: Date.now() } }).then(async () => {
      const { run } = await get('run')
      if (run?.status === 'paused') resume()
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

get('run').then(({ run }) => {
  if (run?.status === 'running') loop()
})
