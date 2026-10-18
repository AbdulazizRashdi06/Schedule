// Fetches one course's sections from EduWave's Advisor Plan page, the same postback its "Sections" button sends.
// Shared by the content script (inside the EduWave tab) and the offscreen page (in the background).
// Returns { rows, fields } — fields is the page's fresh form state, so the next request uses a current __VIEWSTATE.
// Throws Error('signed-out') when EduWave answers with the sign-in page, Error('bad-page') for anything unexpected.
/* exported classyFetchCourse */
async function classyFetchCourse(url, fields, code) {
  const body = new URLSearchParams()
  for (const [k, v] of fields) if (k !== '__EVENTTARGET' && k !== '__EVENTARGUMENT') body.append(k, v)
  body.set('__EVENTTARGET', code + ',')
  body.set('__EVENTARGUMENT', 'GetCourseSections')
  const res = await fetch(url, { method: 'POST', body, credentials: 'include' })
  const html = await res.text()
  const doc = new DOMParser().parseFromString(html, 'text/html')
  if ((res.redirected && /login|logout|default\.aspx/i.test(res.url)) || /login|logout|sign in/i.test(doc.title)) throw new Error('signed-out')
  const form = doc.querySelector('form[action*="AdvisorStudentPlan"]')
  if (!res.ok || !form) throw new Error(/NotAuthori[sz]ed/i.test(res.url + html.slice(0, 4000)) ? 'signed-out' : 'bad-page')

  const to24 = (s) => {
    const m = s.trim().match(/(\d+):(\d+)\s*(AM|PM)?/i)
    if (!m) return s.trim()
    let h = +m[1] % 12
    if (/PM/i.test(m[3] || '')) h += 12
    else if (!m[3]) h = +m[1]
    return String(h).padStart(2, '0') + ':' + m[2]
  }
  const split = (cell) => cell.innerHTML.split(/<br\s*\/?>/i).map((s) => s.replace(/<[^>]+>/g, '').trim()).filter(Boolean)

  const rows = []
  const table = doc.getElementById('ctl00_cphPageBody_gvCourseSections')
  for (const r of table ? [...table.rows].slice(1).filter((r) => r.cells.length >= 7) : []) {
    const section = r.cells[0].textContent.trim()
    const instructor = (r.cells[1].textContent.trim() + ' ' + r.cells[2].textContent.trim()).trim()
    const days = split(r.cells[4])
    const times = split(r.cells[5])
    const rooms = split(r.cells[6])
    for (let k = 0; k < Math.min(days.length, times.length); k++) {
      const d = days[k].match(/(\d{1,2})\/(\d{1,2})\/(\d{4})/)
      if (!d) continue
      const [a, b] = times[k].split('-')
      rows.push({
        code,
        section,
        instructor,
        date: `${d[3]}-${d[2].padStart(2, '0')}-${d[1].padStart(2, '0')}`,
        start: to24(a),
        end: to24(b || a),
        room: rooms[k] || rooms[0] || '',
      })
    }
  }
  return { rows, fields: [...new FormData(form)].filter(([, v]) => typeof v === 'string') }
}
