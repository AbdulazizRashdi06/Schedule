const $ = (id) => document.getElementById(id)
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c])
const ago = (t) => {
  const min = Math.round((Date.now() - t) / 60000)
  if (min < 1) return 'just now'
  if (min < 60) return `${min} min ago`
  if (min < 24 * 60) return `${Math.round(min / 60)} h ago`
  return new Date(t).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
}

async function render() {
  const { token, form, run, last, auto } = await chrome.storage.local.get(['token', 'form', 'run', 'last', 'auto'])
  $('classy-dot').className = 'dot' + (token ? ' on' : '')
  $('classy').innerHTML = token
    ? 'Connected to Classy.'
    : 'Not connected. Sign in to the <a href="https://schedule.tryzarqa.com/admin" target="_blank">Classy dashboard</a> as an owner, then press Connect.'
  $('connect').hidden = !!token

  $('eduwave-dot').className = 'dot' + (form ? ' on' : '')
  $('eduwave').innerHTML = form
    ? `EduWave Advisor Plan seen ${ago(form.at)}.`
    : 'Sign in to <a href="https://eduwave.gutech.edu.om" target="_blank">EduWave</a> and open <b>Advisor Plan</b> through its menu once.'

  const running = run && run.status === 'running'
  $('run-dot').className = 'dot' + (running ? ' on' : run?.status === 'paused' || (last && !last.ok) ? ' bad' : last ? ' on' : '')
  if (run) {
    const pct = Math.floor((run.index / run.codes.length) * 100)
    $('run').innerHTML =
      `${running ? 'Syncing' : 'Paused at'} ${run.index} of ${run.codes.length} courses (${esc(run.semester)}).` +
      `<div class="bar"><span style="width:${pct}%"></span></div>` +
      (run.message ? `<p class="bad-text">${esc(run.message)}</p>` : '')
  } else if (last?.ok) {
    const r = last.result
    $('run').innerHTML =
      `Last sync ${ago(last.at)}: ${r.courses} courses, ${r.classes.toLocaleString()} classes. ` +
      (r.added + r.updated + r.removed === 0 ? 'Nothing changed.' : `${r.added} added, ${r.updated} changed, ${r.removed} removed. <a href="https://schedule.tryzarqa.com/admin/upload" target="_blank">Check and apply in Classy</a>.`) +
      (last.failed ? ` <span class="muted">${last.failed} courses didn’t answer and were kept as they were.</span>` : '')
  } else if (last) {
    $('run').innerHTML = `<span class="bad-text">Last sync ${ago(last.at)} failed: ${esc(last.error)}</span>`
  } else $('run').textContent = 'No sync yet.'

  $('sync').textContent = run?.status === 'paused' ? 'Carry on' : 'Sync now'
  $('sync').disabled = !token || !form || running
  $('stop').hidden = !run
  $('auto').checked = auto !== false
}

async function send(type) {
  $('error').textContent = ''
  const r = await chrome.runtime.sendMessage({ type })
  if (r && !r.ok) $('error').textContent = r.error
  render()
}

$('connect').onclick = () => send('connect')
$('sync').onclick = () => send('sync')
$('stop').onclick = () => send('stop')
$('auto').onchange = (e) => chrome.storage.local.set({ auto: e.target.checked })
chrome.storage.onChanged.addListener(render)
render()
