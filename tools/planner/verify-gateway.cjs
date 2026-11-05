// Runs inside the private Codex gateway container with a synthetic request on stdin.
let input = ''
process.stdin.on('data', chunk => { input += chunk })
process.stdin.on('end', async () => {
  try {
    const request = JSON.parse(input)
    const mode = JSON.parse(request.messages[1].content[0].text).smokeMode
    const response = await fetch('http://127.0.0.1:8090/v1/chat/completions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: input, signal: AbortSignal.timeout(280000) })
    if (!response.ok) throw new Error(`Private gateway returned HTTP ${response.status}`)
    const result = await response.json()
    const extracted = JSON.parse(result.choices[0].message.content)
    const common = extracted.date === '2026-11-12' && extracted.duration === 120 && extracted.courseIds.includes(42)
    const valid = mode === 'image' ? common && extracted.intent === 'proposal' && extracted.start === '10:00' && extracted.end === '12:00' && extracted.room === 'B105'
      : common && extracted.intent === 'find-slots' && extracted.kind === 'class' && extracted.start === null && extracted.end === null
    if (!valid) throw new Error(`Synthetic ${mode} extraction did not match the expected scheduling facts`)
    console.log(`PASS Classy ${mode} extraction: existing ChatGPT login, production schema, date/course/duration verified; no event published.`)
  } catch (error) { console.error(error.message); process.exitCode = 1 }
})
