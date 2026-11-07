import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Loading, Notice, Sk } from '../components/ui'
import { PageTitle, Th } from './AdminLayout'

interface Day {
  day: number; activeUsers: number; peakUsers: number; peakAt: number; firstSample: number; lastSample: number
}
interface Report {
  enabled: boolean; retentionDays: number; since: string; firstSample: string | null
  busiestDay: Day | null; peak: Day | null; requests: number; serverErrors: number; clientErrors: number
  averageResponseMs: number; maxResponseMs: number; registrations: number; studentsRegistered: number
  setup: { step: string; students: number }[]; daily: Day[]
  /** Requests by hour of day (Oman), and per route, already added up on the server. */
  hours: { hour: number; requests: number }[]; routes: { route: string; requests: number }[]
}
interface Live { activeNow: number; activeToday: number }
const count = (n: number) => n.toLocaleString('en-GB')
const date = (seconds: number, full = false) => new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Asia/Muscat', day: 'numeric', month: 'short', ...(full ? { weekday: 'short' } : {}),
}).format(new Date(seconds * 1000))
const time = (seconds: number) => new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Asia/Muscat', hour: '2-digit', minute: '2-digit', hour12: false,
}).format(new Date(seconds * 1000))
const milliseconds = (n: number) => n < 1000 ? `${Math.round(n)} ms` : `${(n / 1000).toFixed(2)} s`
const percent = (n: number, total: number) => total ? `${(n * 100 / total).toFixed(1)}%` : '—'

function feature(route: string) {
  if (route.startsWith('/api/admin')) return 'Admin tools'
  if (route.includes('/auth')) return 'Sign-in & account'
  if (route.includes('/notes')) return 'Class notes'
  if (route.includes('/groups') || route.includes('/join')) return 'Study groups'
  if (route.includes('/timetable')) return 'Timetable'
  if (route.includes('/picks') || route.includes('/options')) return 'Course selection'
  if (route.includes('/slots')) return 'Personal events'
  if (route.includes('/feedback')) return 'Feedback'
  return 'Profile & other requests'
}

export default function Analytics() {
  const [days, setDays] = useState(30)
  const [revision, setRevision] = useState(0)
  const [saved, setSaved] = useState<{ days: number; report: Report; live: Live; at: Date } | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  const current = saved?.days === days ? saved : null
  useEffect(() => {
    let active = true
    let pending = false
    const load = async () => {
      if (pending) return
      pending = true
      setBusy(true)
      try {
        const [report, live] = await Promise.all([api.get<Report>(`/admin/analytics?days=${days}`), api.get<Live>('/admin/stats')])
        if (active) { setSaved({ days, report, live, at: new Date() }); setError('') }
      } catch (e) { if (active) setError(errorText(e)) }
      finally { pending = false; if (active) setBusy(false) }
    }
    void load()
    const timer = setInterval(() => void load(), 60_000)
    return () => { active = false; clearInterval(timer) }
  }, [days, revision])

  const report = current?.report
  const daily = report ? Array.from({ length: days }, (_, i) => {
    const day = Date.parse(report.since) / 1000 + i * 86400
    return { day, sample: report.daily.find(x => x.day === day) }
  }) : []
  const selectedDay = daily.find(x => x.day === selected) ?? daily[daily.length - 1]
  const dayMax = Math.max(1, ...daily.map(x => Math.max(x.sample?.activeUsers ?? 0, x.sample?.peakUsers ?? 0)))
  const hours = report?.hours ?? []
  const features = new Map<string, number>()
  for (const row of report?.routes ?? []) {
    const name = feature(row.route)
    features.set(name, (features.get(name) ?? 0) + row.requests)
  }
  const hourMax = Math.max(1, ...hours.map(x => x.requests))
  const ranked = [...features].sort((a, b) => b[1] - a[1])
  const hasActivity = Boolean(report?.daily.length)

  return <>
    <PageTitle title="Analytics" sub="Aggregate activity across Classy · all semesters · Oman time" action={
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1 text-[13px] font-bold">
          Date range
          <select value={days} onChange={e => { setDays(Number(e.target.value)); setSelected(null) }} className="field h-12 rounded-[12px] bg-paper px-3 text-[15px] font-bold text-navy">
            {[7, 30, 90].map(n => <option key={n} value={n}>Last {n} days</option>)}
          </select>
        </label>
        <Button variant="secondary" busy={busy} onClick={() => setRevision(x => x + 1)}>{busy ? 'Updating…' : 'Refresh'}</Button>
      </div>
    } />
    {error && <div><ErrorText>{error}</ErrorText>{current && <p className="mt-2 text-[14px] font-semibold text-muted">Showing the last successful update. Use Refresh to try again.</p>}</div>}
    {!current && busy && <div aria-busy="true" aria-label="Loading analytics" className="space-y-6"><Sk className="h-24 w-full rounded-xl" /><Sk className="h-60 w-full rounded-xl" /><Loading rows={3} /></div>}
    {report && current && <>
      {!report.enabled && <Notice tone="warn">Collection is paused. These are previously recorded totals.</Notice>}
      <div className="flex flex-wrap items-center justify-between gap-2 border-y border-navy/15 py-3 text-[14px] font-semibold">
        <p><strong className="text-navy tabular-nums">{count(current.live.activeNow)}</strong> active in the last 5 minutes <span aria-hidden="true">·</span> <strong className="text-navy tabular-nums">{count(current.live.activeToday)}</strong> today</p>
        <p className="text-muted">Updated {time(current.at.getTime() / 1000)} · refreshes every minute</p>
      </div>
      <section aria-label="Activity highlights" className="grid gap-6 border-b border-navy/15 pb-7 md:grid-cols-2">
        <div>
          <h2 className="text-[15px] font-bold text-muted">Busiest observed day</h2>
          <p className="mt-2 text-[30px] leading-tight font-extrabold tracking-[-0.03em]">{report.busiestDay ? date(report.busiestDay.day, true) : 'No samples yet'}</p>
          <p className="mt-2 text-[15px] font-semibold">{report.busiestDay ? `${count(report.busiestDay.activeUsers)} students active that day` : 'This will appear after students use the app.'}</p>
        </div>
        <div>
          <h2 className="text-[15px] font-bold text-muted">Most people active together</h2>
          <p className="mt-2 text-[30px] leading-tight font-extrabold tracking-[-0.03em]">{report.peak ? `${count(report.peak.peakUsers)} students` : 'No samples yet'}</p>
          <p className="mt-2 text-[15px] font-semibold">{report.peak ? `${date(report.peak.peakAt, true)} at ${time(report.peak.peakAt)} · five-minute window` : 'Peak activity will appear here.'}</p>
        </div>
      </section>

      <section aria-labelledby="daily-title" className="space-y-4">
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div><h2 id="daily-title" className="text-[24px] font-extrabold tracking-[-0.02em]">Daily activity</h2><p className="mt-1 text-[14px] font-semibold text-muted">Choose a day to inspect it. Gaps mean no sample, not zero people.</p></div>
          <div className="flex gap-4 text-[13px] font-bold"><span className="flex items-center gap-2"><span className="size-2.5 bg-navy" />Active students</span><span className="flex items-center gap-2"><span className="size-2.5 bg-sky" />Peak together</span></div>
        </div>
        {hasActivity ? <>
          <div className="overflow-x-auto pb-2">
            <div className="flex h-48 items-end gap-1 border-b border-navy/25" style={{ minWidth: Math.max(260, days * 12) }}>
              {daily.map(({ day, sample }) => <button key={day} type="button" onClick={() => setSelected(day)} aria-pressed={selectedDay?.day === day}
                aria-label={`${date(day, true)}: ${sample ? `${sample.activeUsers} active students, peak ${sample.peakUsers}` : 'no sample'}`}
                className="group flex h-full min-w-0 flex-1 items-end justify-center gap-px rounded-t-sm px-px pt-2 outline-offset-4 hover:bg-navy/5 focus-visible:outline-2 focus-visible:outline-navy"
                style={{ backgroundColor: selectedDay?.day === day ? 'var(--color-paper)' : undefined }}>
                {sample ? <><span className="w-1/2 bg-navy" style={{ height: `${Math.max(1, sample.activeUsers / dayMax * 100)}%` }} /><span className="w-1/2 bg-sky" style={{ height: `${Math.max(1, sample.peakUsers / dayMax * 100)}%` }} /></> : <span className="h-1 w-full border-t border-dashed border-navy/25" />}
              </button>)}
            </div>
          </div>
          <div className="flex justify-between text-[12px] font-bold text-muted"><span>{date(daily[0].day)}</span><span>Maximum: {count(dayMax)} students</span><span>{date(daily[daily.length - 1].day)}</span></div>
          <p aria-live="polite" className="min-h-6 text-[15px] font-semibold">{selectedDay && <><strong>{date(selectedDay.day, true)}</strong>{selectedDay.sample ? ` — ${count(selectedDay.sample.activeUsers)} active students; peak ${count(selectedDay.sample.peakUsers)} at ${time(selectedDay.sample.peakAt)}.` : ' — no activity sample recorded.'}</>}</p>
          <details className="border-y border-navy/15 py-3">
            <summary className="cursor-pointer text-[14px] font-bold">Daily values</summary>
            <div className="mt-3 max-h-72 overflow-auto"><table className="w-full text-[14px] tabular-nums"><caption className="sr-only">Daily active students and peak five-minute activity</caption><thead><tr><Th>Day</Th><Th right>Active</Th><Th right>Peak</Th><Th right>Peak time</Th></tr></thead><tbody>{[...daily].reverse().map(x => <tr key={x.day} className="border-t border-navy/10"><th scope="row" className="px-2 py-2 text-left font-semibold">{date(x.day)}</th><td className="px-2 py-2 text-right">{x.sample ? count(x.sample.activeUsers) : '—'}</td><td className="px-2 py-2 text-right">{x.sample ? count(x.sample.peakUsers) : '—'}</td><td className="px-2 py-2 text-right">{x.sample ? time(x.sample.peakAt) : '—'}</td></tr>)}</tbody></table></div>
          </details>
        </> : <Notice>Activity history is starting from deployment. There are no student activity samples in this date range yet.</Notice>}
      </section>

      <section aria-labelledby="traffic-title" className="space-y-4 border-t border-navy/15 pt-6">
        <h2 id="traffic-title" className="text-[24px] font-extrabold tracking-[-0.02em]">Traffic & reliability</h2>
        <dl className="grid grid-cols-2 gap-x-6 gap-y-5 lg:grid-cols-4">
          {[
            ['API requests', count(report.requests)], ['Average response', report.requests ? milliseconds(report.averageResponseMs) : '—'],
            ['Server errors', `${count(report.serverErrors)} (${percent(report.serverErrors, report.requests)})`],
            ['Longest response', report.requests ? milliseconds(report.maxResponseMs) : '—'],
          ].map(([label, value]) => <div key={label}><dt className="text-[14px] font-bold text-muted">{label}</dt><dd className="mt-1 text-[22px] font-extrabold tabular-nums">{value}</dd></div>)}
        </dl>
        <p className="text-[13px] font-semibold text-muted">Requests include background calls and admin traffic. They are not visits. Response time measures the server, not time spent in the app.</p>
        {report.requests > 0 && <>
          <div role="img" aria-label={hours.map(x => `${String(x.hour).padStart(2, '0')}:00 ${count(x.requests)} requests`).join('; ')}>
            <div aria-hidden="true" className="flex h-28 items-end gap-1 border-b border-navy/25">{hours.map(x => <span key={x.hour} title={`${String(x.hour).padStart(2, '0')}:00 · ${count(x.requests)} requests`} className="flex-1 bg-navy/75" style={{ height: x.requests ? `${Math.max(2, x.requests / hourMax * 100)}%` : 0 }} />)}</div>
            <div aria-hidden="true" className="mt-2 flex justify-between text-[12px] font-bold text-muted">{['00:00', '06:00', '12:00', '18:00', '23:00'].map(x => <span key={x}>{x}</span>)}</div>
          </div>
          <p className="text-[13px] font-semibold text-muted">Requests by hour of day, summed across the selected dates. All hours are Oman time.</p>
        </>}
      </section>

      <div className="grid gap-8 border-t border-navy/15 pt-6 xl:grid-cols-2">
        <section><h2 className="mb-3 text-[24px] font-extrabold tracking-[-0.02em]">Feature requests</h2>{ranked.length ? <table className="w-full text-[14px]"><caption className="sr-only">Request totals grouped by app feature</caption><thead><tr><Th>Feature</Th><Th right>Requests</Th><Th right>Share</Th></tr></thead><tbody>{ranked.map(([name, requests]) => <tr key={name} className="border-t border-navy/15"><th scope="row" className="py-3 pr-2 text-left font-semibold">{name}</th><td className="px-2 py-3 text-right font-bold tabular-nums">{count(requests)}</td><td className="px-2 py-3 text-right tabular-nums">{percent(requests, report.requests)}</td></tr>)}</tbody></table> : <p className="text-[15px] font-semibold text-muted">Feature totals will appear after the first requests.</p>}</section>
        <section><h2 className="mb-3 text-[24px] font-extrabold tracking-[-0.02em]">Student accounts</h2><dl className="space-y-3 border-b border-navy/15 pb-4">
          <div className="flex justify-between gap-3"><dt className="font-semibold">Registered accounts · current</dt><dd className="font-extrabold tabular-nums">{count(report.studentsRegistered)}</dd></div>
          <div className="flex justify-between gap-3"><dt className="font-semibold">Registered in this date range</dt><dd className="font-extrabold tabular-nums">{count(report.registrations)}</dd></div>
          <div className="flex justify-between gap-3"><dt className="font-semibold">Client errors · this date range</dt><dd className="font-extrabold tabular-nums">{count(report.clientErrors)}</dd></div>
        </dl><h3 className="mt-5 text-[15px] font-bold">Current setup progress</h3><p className="mt-1 text-[13px] font-semibold text-muted">Current account state, across all semesters.</p><dl className="mt-3 space-y-2">{report.setup.map(x => <div key={x.step} className="flex justify-between gap-3 text-[14px]"><dt className="font-semibold">{({ done: 'Setup complete', core: 'Choosing core courses', ls: 'Choosing Life Skills courses', electives: 'Choosing electives', major: 'Choosing a major', year: 'Choosing a year', waiting: 'Waiting for a semester', 'not-started': 'Not started' } as Record<string, string>)[x.step] ?? x.step}</dt><dd className="font-bold tabular-nums">{count(x.students)}</dd></div>)}</dl></section>
      </div>
      <p className="border-t border-navy/15 pt-4 text-[13px] leading-relaxed font-semibold text-muted">{report.firstSample ? `First student activity sample in this range: ${date(Date.parse(report.firstSample) / 1000, true)} at ${time(Date.parse(report.firstSample) / 1000)}. ` : ''}History is retained for {report.retentionDays} days. Peaks are observed estimates; partial days and outages can leave gaps. This dashboard uses aggregate records, without student analytics prompts or personal session tracking.</p>
    </>}
  </>
}
