// Runs on EduWave's Advisor Plan page. Hands its form to the extension (so syncs can run in the background later),
// and fetches courses from inside this tab when the extension asks, which always has your signed-in session.
/* global classyFetchCourse */
const form = document.forms[0]
if (form) {
  const fields = [...new FormData(form)].filter(([, v]) => typeof v === 'string')
  chrome.runtime.sendMessage({ type: 'form', url: location.href, fields })
}

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type !== 'tab-fetch') return
  classyFetchCourse(msg.url, msg.fields, msg.code).then(
    (r) => reply({ ok: true, ...r }),
    (e) => reply({ ok: false, error: e.message }),
  )
  return true
})
