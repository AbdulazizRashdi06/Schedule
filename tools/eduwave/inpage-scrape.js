// EduWave timetable scrape, run inside a signed-in EduWave tab on the Advisor Plan page.
// Inject this whole file (for example with the browser pane's JavaScript tool), then:
//   ewStart(['CS 1 107', 'LS 1 003', ...])  start fetching, one course a second, in the background
//   ewStatus()                              { status, done, total, current, classes, failed, error }
//   ewResume()                              carry on after a sign-out (sign in, reopen Advisor Plan, inject again, then resume)
//   ewLines()                               the compact lines, once done ("CODE|section|teacher|HH:MM-HH:MM|room|days")
//   ewChunk(from, to)                       lines[from..to) joined with newlines, for copying out in pieces
//   await ewDiff(oldHashesCsv)              { added: [lines], gone: n } against last run's fingerprints
// It only reads course sections (the same request as the page's own "Sections" button) and never submits anything else.
(() => {
  const EPOCH = Date.UTC(2026, 7, 1) // day 0 of the "days" field: 1 Aug 2026
  const GAP_MS = 1000
  const form = document.forms[0]
  if (!form || !/AdvisorStudentPlan/i.test(location.pathname)) throw new Error('Open Advisor Plan through the EduWave menu first.')

  const prev = window.__ew
  const st = (window.__ew = prev ?? { status: 'idle', codes: [], i: 0, rows: [], failed: [], error: '', fields: null })
  st.fields = [...new FormData(form)].filter(([, v]) => typeof v === 'string')

  const to24 = (s) => {
    const m = s.trim().match(/(\d+):(\d+)\s*(AM|PM)?/i)
    if (!m) return s.trim()
    let h = +m[1] % 12
    if (/PM/i.test(m[3] || '')) h += 12
    else if (!m[3]) h = +m[1]
    return String(h).padStart(2, '0') + ':' + m[2]
  }
  const split = (cell) => cell.innerHTML.split(/<br\s*\/?>/i).map((s) => s.replace(/<[^>]+>/g, '').trim()).filter(Boolean)

  async function fetchCourse(code) {
    const body = new URLSearchParams()
    for (const [k, v] of st.fields) if (k !== '__EVENTTARGET' && k !== '__EVENTARGUMENT') body.append(k, v)
    body.set('__EVENTTARGET', code + ',')
    body.set('__EVENTARGUMENT', 'GetCourseSections')
    const res = await fetch(location.href, { method: 'POST', body, credentials: 'include' })
    const html = await res.text()
    const doc = new DOMParser().parseFromString(html, 'text/html')
    if (/login|logout|sign in/i.test(doc.title) || /NotAuthori[sz]ed/i.test(res.url)) throw new Error('signed-out')
    const page = doc.querySelector('form[action*="AdvisorStudentPlan"]')
    if (!res.ok || !page) throw new Error('bad-page')
    st.fields = [...new FormData(page)].filter(([, v]) => typeof v === 'string') // fresh __VIEWSTATE for the next request
    const rows = []
    const table = doc.getElementById('ctl00_cphPageBody_gvCourseSections')
    for (const r of table ? [...table.rows].slice(1).filter((r) => r.cells.length >= 7) : []) {
      const section = r.cells[0].textContent.trim()
      const teacher = (r.cells[1].textContent.trim() + ' ' + r.cells[2].textContent.trim()).trim()
      const days = split(r.cells[4])
      const times = split(r.cells[5])
      const rooms = split(r.cells[6])
      for (let k = 0; k < Math.min(days.length, times.length); k++) {
        const d = days[k].match(/(\d{1,2})\/(\d{1,2})\/(\d{4})/)
        if (!d) continue
        const [a, b] = times[k].split('-')
        rows.push({ code, section, teacher, day: Math.round((Date.UTC(+d[3], +d[2] - 1, +d[1]) - EPOCH) / 864e5), time: `${to24(a)}-${to24(b || a)}`, room: rooms[k] || rooms[0] || '' })
      }
    }
    return rows
  }

  async function loop() {
    st.status = 'running'
    st.error = ''
    while (st.i < st.codes.length) {
      const code = st.codes[st.i]
      st.current = code
      let rows = null
      for (let attempt = 0; attempt < 2 && !rows; attempt++) {
        try {
          rows = await fetchCourse(code)
        } catch (e) {
          if (e.message === 'signed-out') {
            st.status = 'paused'
            st.error = 'EduWave signed out. Sign in, open Advisor Plan through the menu, inject this file again, then ewResume().'
            return
          }
          await new Promise((r) => setTimeout(r, 3000))
        }
      }
      if (rows) st.rows.push(...rows)
      else st.failed.push(code)
      st.i++
      await new Promise((r) => setTimeout(r, GAP_MS))
    }
    st.status = 'done'
    st.current = ''
  }

  /** "50w14" = day 50 and every 7 days after it, 14 days in all; single days stay plain. Joined with ".". */
  function encodeDays(days) {
    const d = [...new Set(days)].sort((a, b) => a - b)
    const parts = []
    for (let i = 0; i < d.length; ) {
      let n = 1
      while (i + n < d.length && d[i + n] === d[i] + 7 * n) n++
      if (n >= 3) {
        parts.push(`${d[i]}w${n}`)
        i += n
      } else {
        parts.push(String(d[i]))
        i++
      }
    }
    return parts.join('.')
  }

  window.ewStart = (codes) => {
    Object.assign(st, { codes: [...codes], i: 0, rows: [], failed: [], status: 'idle', error: '' })
    loop()
    return `Started: ${codes.length} courses, about ${Math.ceil((codes.length * 1.3) / 60)} minutes.`
  }
  window.ewResume = () => {
    if (st.status === 'running') return 'Already running.'
    loop()
    return `Resuming at ${st.i + 1} of ${st.codes.length}.`
  }
  window.ewStatus = () => ({ status: st.status, done: st.i, total: st.codes.length, current: st.current, classes: st.rows.length, failed: st.failed, error: st.error })
  window.ewLines = () => {
    const groups = new Map()
    for (const r of st.rows) {
      const key = [r.code, r.section, r.teacher, r.time, r.room].join('|')
      groups.set(key, [...(groups.get(key) ?? []), r.day])
    }
    return [...groups].map(([k, days]) => `${k}|${encodeDays(days)}`).sort()
  }
  window.ewChunk = (from, to) => window.ewLines().slice(from, to).join('\n')
  window.ewDiff = async (oldCsv) => {
    const old = new Set(String(oldCsv).split(',').map((s) => s.trim()).filter(Boolean))
    const sha = async (s) => [...new Uint8Array(await crypto.subtle.digest('SHA-1', new TextEncoder().encode(s)))].map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 10)
    const lines = window.ewLines()
    const hashes = await Promise.all(lines.map(sha))
    const now = new Set(hashes)
    return { added: lines.filter((_, i) => !old.has(hashes[i])), gone: [...old].filter((h) => !now.has(h)).length, total: lines.length }
  }
  return 'EduWave scrape ready: ewStart(codes).'
})()
