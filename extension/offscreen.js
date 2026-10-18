// A hidden page the background worker uses to fetch EduWave when no Advisor Plan tab is open (it needs a DOM to read the replies).
/* global classyFetchCourse */
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type !== 'offscreen-fetch') return
  classyFetchCourse(msg.url, msg.fields, msg.code).then(
    (r) => reply({ ok: true, ...r }),
    (e) => reply({ ok: false, error: e.message }),
  )
  return true
})
