import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/lib/engagementTracker.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText

function browser() {
  let now = 0
  let next = 0
  const calls = []
  const timers = new Map()
  const target = () => {
    const listeners = new Map()
    return {
      addEventListener(type, fn) { if (!listeners.has(type)) listeners.set(type, new Set()); listeners.get(type).add(fn) },
      removeEventListener(type, fn) { listeners.get(type)?.delete(fn) },
      fire(type) { for (const fn of listeners.get(type) ?? []) fn() },
    }
  }
  const window = target()
  const document = { ...target(), visibilityState: 'visible', hasFocus: () => true }
  window.setInterval = (fn, ms) => { const id = ++next; timers.set(id, { fn, ms, at: now + ms }); return id }
  window.clearInterval = id => timers.delete(id)
  const exports = {}
  vm.runInNewContext(compiled, {
    exports, require: () => ({ API_BASE: '' }), window, document, performance: { now: () => now },
    crypto: { randomUUID: () => `00000000-0000-4000-8000-${String(++next).padStart(12, '0')}` },
    fetch: async (_url, options) => { calls.push(JSON.parse(options.body)); return { status: 204, ok: true } },
  })
  const settle = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve() }
  return {
    ...exports, document, window, calls, settle,
    async advance(ms) {
      const until = now + ms
      while (true) {
        const timer = [...timers.values()].filter(x => x.at <= until).sort((a, b) => a.at - b.at)[0]
        if (!timer) break
        now = timer.at
        timer.at += timer.ms
        timer.fn()
        await settle()
      }
      now = until
      await settle()
    },
  }
}

test('screen categories strip private identifiers and exclude admin pages', () => {
  const b = browser()
  assert.equal(b.screenFor('/course/private-id'), 'course')
  assert.equal(b.screenFor('/join/private-code'), 'join')
  assert.equal(b.screenFor('/admin/students/123'), null)
})

test('active reading stops accruing after 60 seconds idle and cleanup stops all reports', async () => {
  const b = browser()
  const runner = b.startTracking('/', () => assert.fail('unexpected denial'))
  await b.settle()
  await b.advance(120000)
  assert.equal(b.calls.at(-1).screens[0].activeMs, 60000)
  const before = b.calls.length
  runner.stop()
  b.window.fire('pointerdown')
  await b.advance(60000)
  assert.equal(b.calls.length, before)
})

test('hidden time is excluded and navigation sends category counters only', async () => {
  const b = browser()
  const runner = b.startTracking('/', () => assert.fail('unexpected denial'))
  await b.settle()
  await b.advance(20000)
  b.document.visibilityState = 'hidden'
  b.document.fire('visibilitychange')
  await b.settle()
  await b.advance(60000)
  assert.equal(b.calls.at(-1).screens[0].activeMs, 20000)
  b.document.visibilityState = 'visible'
  b.document.fire('visibilitychange')
  runner.navigate('/course/secret-course-id')
  await b.advance(15000)
  b.document.visibilityState = 'hidden'
  b.document.fire('visibilitychange')
  await b.settle()
  assert.equal(b.calls.at(-1).screens.find(x => x.screen === 'course').activeMs, 15000)
  assert.ok(!JSON.stringify(b.calls).includes('secret-course-id'))
  runner.stop()
})
