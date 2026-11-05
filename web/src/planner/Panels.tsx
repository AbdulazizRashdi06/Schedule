import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api, errorText, store } from '../lib/api'
import type { Course } from '../lib/types'
import { Button, ErrorText, Select, TextField, cx } from '../components/ui'
import { addDays, isoDate } from '../lib/slots'
import { campusToday, dateValue, type AiJob, type AiResult, type Analysis, type Candidate, type Proposal, type SlotRequest, type Spec, type Target } from './types'

export function TargetPicker({ courses, targets, onChange }: { courses: Course[]; targets: Target[]; onChange: (targets: Target[]) => void }) {
  const [query, setQuery] = useState('')
  const filtered = courses.filter(c => `${c.code} ${c.name}`.toLowerCase().includes(query.toLowerCase()))
  return <fieldset className="min-w-0"><legend className="mb-2 text-[14px] font-extrabold">Courses and sections</legend>
    <TextField label="Find a course" value={query} onChange={e => setQuery(e.target.value)} placeholder="Code or course name" />
    <div className="mt-2 max-h-64 overflow-y-auto rounded-[14px] border border-navy/20">
      {filtered.map(c => { const chosen = targets.find(t => t.courseId === c.id); return <div key={c.id} className="border-b border-navy/10 px-3 py-1 last:border-0">
        <label className="flex min-h-11 cursor-pointer items-center gap-2 text-[13px] font-bold"><input type="checkbox" checked={!!chosen} onChange={e => onChange(e.target.checked ? [...targets, { courseId: c.id, sectionIds: [] }] : targets.filter(t => t.courseId !== c.id))} className="size-4 shrink-0 accent-navy" /><span>{c.code} · {c.name}</span></label>
        {chosen && <div className="mb-1 flex flex-wrap gap-1 pl-6"><button type="button" onClick={() => onChange(targets.map(t => t.courseId === c.id ? { ...t, sectionIds: [] } : t))} aria-pressed={chosen.sectionIds.length === 0} className={cx('min-h-11 rounded-full px-2 text-[12px] font-bold', chosen.sectionIds.length === 0 ? 'bg-navy text-cream' : 'tint')}>All sections</button>
          {c.sections.map(s => <button key={s.id} type="button" aria-pressed={chosen.sectionIds.includes(s.id)} className={cx('min-h-11 rounded-full px-2 text-[12px] font-bold', chosen.sectionIds.includes(s.id) ? 'bg-navy text-cream' : 'tint')} onClick={() => onChange(targets.map(t => t.courseId === c.id ? { ...t, sectionIds: t.sectionIds.includes(s.id) ? t.sectionIds.filter(id => id !== s.id) : [...t.sectionIds, s.id] } : t))}>Sec {s.number}</button>)}
        </div>}
      </div> })}
      {filtered.length === 0 && <p className="p-3 text-[14px] text-muted">No matching courses.</p>}
    </div><p className="mt-2 text-[12px] text-muted">Students receive this event through the courses and sections they picked.</p>
  </fieldset>
}

export function Warnings({ analysis }: { analysis: Analysis }) {
  return <div className="flex flex-col gap-2 rounded-[14px] border border-navy/20 p-3 text-[13px]">
    <h3 className="font-extrabold">{analysis.conflicts.length ? `${analysis.conflicts.length} scheduling warning${analysis.conflicts.length === 1 ? '' : 's'}` : 'No recorded academic clashes'}</h3>
    {analysis.conflicts.map((c, i) => <p key={i}><strong>{c.date}:</strong> {c.message}{c.courses.length > 0 && ` · ${c.courses.join(', ')}`}</p>)}
    {analysis.coverageWarnings.map(w => <p key={w} className="font-semibold text-alert">{w}</p>)}
    <p className="text-muted">Checks use recorded academic timetables, not private appointments.</p>
  </div>
}

export function ProposalEditor({ initial, courses, base, onSaved, onPublished, onClose, onWithdraw }: { initial: { spec: Spec; proposal?: Proposal; eventId?: number; baseEventVersion?: number }; courses: Course[]; base: string; onSaved: () => Promise<void>; onPublished: () => Promise<void>; onClose: () => void; onWithdraw?: () => void }) {
  const [spec, setSpec] = useState(initial.spec)
  const [proposal, setProposal] = useState(initial.proposal)
  const [analysis, setAnalysis] = useState<Analysis | null>(null)
  const [explanation, setExplanation] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [busy, setBusy] = useState(false), [error, setError] = useState(''), [dirty, setDirty] = useState(!initial.proposal)
  const change = (values: Partial<Spec>) => { setSpec(s => ({ ...s, ...values })); setDirty(true); setAnalysis(null); setAcknowledged(false) }
  const save = async (e?: FormEvent) => {
    e?.preventDefault(); setBusy(true); setError('')
    try {
      const body = { spec, eventId: initial.eventId ?? proposal?.eventId ?? null, baseEventVersion: initial.baseEventVersion ?? proposal?.baseEventVersion ?? 0, version: proposal?.version ?? 0 }
      const p = proposal ? await api.put<Proposal>(`${base}/proposals/${proposal.id}`, body) : await api.post<Proposal>(`${base}/proposals`, body)
      setProposal(p); setDirty(false); await onSaved()
      setAnalysis(await api.post<Analysis>(`${base}/proposals/${p.id}/analyze`))
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }
  const publish = async () => {
    if (!proposal || !analysis) return
    setBusy(true); setError('')
    try { await api.post(`${base}/proposals/${proposal.id}/publish`, { version: proposal.version, analysisToken: analysis.token, explanation }); await onPublished(); onClose() }
    catch (e) { setError(errorText(e)); setAnalysis(null) } finally { setBusy(false) }
  }
  const warnings = !!analysis && (analysis.conflicts.length > 0 || analysis.coverageWarnings.length > 0)
  return <form onSubmit={save} className="flex min-w-0 flex-col gap-4" aria-label="Scheduling proposal">
    <div className="flex items-center justify-between gap-2"><h2 className="text-[24px] font-extrabold">{proposal ? 'Edit draft' : initial.eventId ? 'Propose a change' : 'New proposal'}</h2><button type="button" onClick={onClose} className="tint min-h-11 rounded-full px-3 text-[13px] font-bold">Close</button></div>
    <p className="text-[13px] font-semibold text-muted">Draft changes stay in this workspace until a rep publishes them. Times are in Oman.</p>
    <label className="flex flex-col gap-1 text-[13px] font-bold">Event type<Select value={spec.kind} disabled={!!spec.source} onChange={e => change({ kind: e.target.value as Spec['kind'], repeatUntil: null })}><option value="exam">Exam</option><option value="class">Class</option></Select></label>
    {spec.source && <><p className="rounded-[12px] bg-ice p-3 text-[13px] font-semibold">Original class: {spec.source.date} · {spec.source.start}–{spec.source.end}. The university timetable is preserved.</p><label className="flex flex-col gap-1 text-[13px] font-bold">Class change<Select value={spec.action} onChange={e => change({ action: e.target.value as Spec['action'], ...(e.target.value === 'cancel' ? { date: spec.source!.date } : {}) })}><option value="replace">Move / update class</option><option value="cancel">Cancel class</option></Select></label></>}
    <TextField label="Title" required maxLength={120} value={spec.title} onChange={e => change({ title: e.target.value })} placeholder="Midterm exam" />
    <TextField label={spec.action === 'cancel' ? 'Cancellation date' : 'Date'} required type="date" value={spec.date} disabled={spec.action === 'cancel'} onChange={e => change({ date: e.target.value })} />
    <div className="grid grid-cols-2 gap-2"><TextField label="Starts" type="time" required value={spec.start} onChange={e => change({ start: e.target.value })} /><TextField label="Ends" type="time" required value={spec.end} onChange={e => change({ end: e.target.value })} /></div>
    {spec.kind === 'class' && <TextField label="Repeat weekly until (optional)" type="date" min={spec.date} value={spec.repeatUntil ?? ''} onChange={e => change({ repeatUntil: e.target.value || null })} />}
    <TextField label="Room / location" maxLength={100} value={spec.room} onChange={e => change({ room: e.target.value })} />
    {!spec.source && <TargetPicker courses={courses} targets={spec.targets} onChange={targets => change({ targets })} />}
    <label className="flex flex-col gap-1 text-[13px] font-bold">Notes<textarea maxLength={2000} value={spec.notes ?? ''} onChange={e => change({ notes: e.target.value })} className="field min-h-20 rounded-[14px] bg-paper p-3 text-[16px] font-semibold" /></label>
    <ErrorText>{error}</ErrorText>
    <Button type="submit" busy={busy} disabled={!spec.targets.length || !spec.date || !spec.start || !spec.end || spec.end <= spec.start}>{dirty || !analysis ? 'Save draft & check conflicts' : 'Recheck conflicts'}</Button>
    {analysis && !dirty && <><Warnings analysis={analysis} />{warnings && <><label className="flex min-h-11 items-start gap-2 text-[13px] font-bold"><input type="checkbox" checked={acknowledged} onChange={e => setAcknowledged(e.target.checked)} className="mt-1 size-5 shrink-0 accent-navy" />I reviewed these warnings and want to publish anyway.</label><TextField label="Why publish despite the warnings?" maxLength={1000} value={explanation} onChange={e => setExplanation(e.target.value)} /></>}<Button type="button" onClick={publish} busy={busy} disabled={warnings && (!acknowledged || !explanation.trim())}>Publish to students</Button></>}
    {proposal && <Button type="button" variant="danger" disabled={busy} onClick={async () => { setBusy(true); try { await api.del(`${base}/proposals/${proposal.id}?version=${proposal.version}`); await onSaved(); onClose() } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>Discard draft</Button>}
    {onWithdraw && <Button type="button" variant="danger" onClick={onWithdraw}>Withdraw this published event…</Button>}
  </form>
}

export function SlotFinder({ base, targets, initial, onPick }: { base: string; targets: Target[]; initial?: Partial<SlotRequest>; onPick: (spec: Spec) => void }) {
  const [request, setRequest] = useState<SlotRequest>({ targets, kind: 'exam', from: campusToday(), to: isoDate(addDays(dateValue(campusToday()), 14)), duration: 120, weekdays: [0, 1, 2, 3, 4], start: '08:00', end: '18:15', room: '', repeatUntil: null, ...initial })
  const [results, setResults] = useState<{ candidates: Candidate[]; coverage: string } | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const change = (values: Partial<SlotRequest>) => { setRequest(r => ({ ...r, ...values })); setResults(null) }
  const find = async (e: FormEvent) => { e.preventDefault(); setBusy(true); setError(''); try { setResults(await api.post(`${base}/slots`, { ...request, targets })) } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }
  return <form onSubmit={find} className="flex flex-col gap-4" aria-label="Smart slot finder"><h2 className="text-[24px] font-extrabold">Find a slot</h2><p className="text-[13px] text-muted">Compare the courses and sections selected in the calendar filters. All times are in Oman.</p>
    <label className="flex flex-col gap-1 text-[13px] font-bold">Mode<Select value={request.kind} onChange={e => change({ kind: e.target.value as SlotRequest['kind'], repeatUntil: null })}><option value="exam">Exam</option><option value="class">Class</option></Select></label>
    <div className="grid grid-cols-2 gap-2"><TextField label="From date" type="date" required value={request.from} onChange={e => change({ from: e.target.value })} /><TextField label="Through date" type="date" required min={request.from} value={request.to} onChange={e => change({ to: e.target.value })} /></div>
    <TextField label="Duration (minutes)" type="number" required min={15} max={600} step={15} value={request.duration} onChange={e => change({ duration: Number(e.target.value) })} />
    <fieldset><legend className="mb-1 text-[13px] font-bold">Allowed weekdays</legend><div className="flex flex-wrap gap-1">{['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map((day, i) => <button key={day} type="button" aria-pressed={request.weekdays.includes(i)} onClick={() => change({ weekdays: request.weekdays.includes(i) ? request.weekdays.filter(d => d !== i) : [...request.weekdays, i] })} className={cx('min-h-11 rounded-full px-2 text-[12px] font-bold', request.weekdays.includes(i) ? 'bg-navy text-cream' : 'tint')}>{day}</button>)}</div></fieldset>
    <div className="grid grid-cols-2 gap-2"><TextField label="Earliest start" type="time" required value={request.start} onChange={e => change({ start: e.target.value })} /><TextField label="Latest finish" type="time" required value={request.end} onChange={e => change({ end: e.target.value })} /></div>
    <TextField label="Room (optional)" value={request.room} maxLength={100} onChange={e => change({ room: e.target.value })} />
    {request.kind === 'class' && <TextField label="Repeat weekly until (optional)" type="date" min={request.to} value={request.repeatUntil ?? ''} onChange={e => change({ repeatUntil: e.target.value || null })} />}
    <ErrorText>{error}</ErrorText><Button type="submit" busy={busy} disabled={targets.length === 0}>Find common free time</Button>
    {results && <><p className="text-[12px] text-muted">{results.coverage}</p>{results.candidates.length === 0 && <p role="status">No candidates in these hours. Try a wider date range or shorter duration.</p>}{results.candidates.map((c, i) => <section key={`${c.spec.date}-${c.spec.start}`} className="flex flex-col gap-2 border-t border-navy/20 pt-4"><h3 className="font-extrabold">{i + 1}. {c.spec.date} · {c.spec.start}–{c.spec.end}</h3><p className="text-[13px]">{c.explanation}</p><Warnings analysis={c.analysis} /><Button type="button" variant="secondary" onClick={() => onPick(c.spec)}>Preview & make proposal</Button></section>)}</>}
  </form>
}

export function AiAssistant({ base, onUse }: { base: string; onUse: (result: AiResult) => void }) {
  const [text, setText] = useState(''), [images, setImages] = useState<string[]>([]), [fileNames, setFileNames] = useState<string[]>([])
  const [job, setJob] = useState<AiJob | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const alive = useRef(true)
  const pollKey = `${base}:ai-job`
  const jobId = job?.id
  const jobStatus = job?.status
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  useEffect(() => {
    const id = jobId ?? store.get(pollKey)
    if (!id || (jobStatus && !['queued', 'running'].includes(jobStatus))) return
    let active = true
    const poll = () => api.get<AiJob>(`${base}/ai-jobs/${id}`).then(value => { if (!active) return; setJob(value); if (!['queued', 'running'].includes(value.status)) store.set(pollKey, '') }, e => { if (active) { setError(errorText(e)); store.set(pollKey, '') } })
    void poll(); const timer = setInterval(poll, 2000)
    return () => { active = false; clearInterval(timer) }
  }, [base, jobId, jobStatus, pollKey])
  const submit = async (e: FormEvent) => { e.preventDefault(); setBusy(true); setError(''); try { const next = await api.post<AiJob>(`${base}/ai-jobs`, { text, images }); if (alive.current) { setJob(next); store.set(pollKey, next.id) } } catch (e) { setError(errorText(e)) } finally { if (alive.current) setBusy(false) } }
  const reading = job && ['queued', 'running'].includes(job.status)
  return <form onSubmit={submit} className="flex flex-col gap-4" aria-label="AI scheduling assistant"><h2 className="text-[24px] font-extrabold">Turn a message into a plan</h2><p className="text-[13px] text-muted">Paste the lecturer's message, attach a screenshot, or ask for a free class or exam slot. You review every result before publishing.</p>
    <label className="flex flex-col gap-1 text-[13px] font-bold">Message or scheduling request<textarea required maxLength={20000} value={text} onChange={e => setText(e.target.value)} placeholder="Find a two-hour class slot next Thursday for AI Year 2 without conflicts." className="field min-h-36 rounded-[14px] bg-paper p-3 text-[16px] font-semibold" /></label>
    <label className="flex flex-col gap-2 text-[13px] font-bold">Text file or screenshots<input type="file" multiple accept=".txt,text/plain,image/png,image/jpeg" disabled={!!reading} className="min-h-11 max-w-full text-[12px]" onChange={async e => {
      setError(''); const files = [...(e.target.files ?? [])]
      if (files.reduce((sum, f) => sum + f.size, 0) > 6 * 1024 * 1024 || files.filter(f => f.type.startsWith('image/')).length > 3) { setError('Attach up to three PNG/JPEG images, under 6 MB combined.'); return }
      try {
        const texts = await Promise.all(files.filter(f => f.type === 'text/plain' || f.name.endsWith('.txt')).map(f => f.text()))
        const pictures = await Promise.all(files.filter(f => ['image/png', 'image/jpeg'].includes(f.type)).map(f => new Promise<string>((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(String(reader.result)); reader.onerror = reject; reader.readAsDataURL(f) })))
        if (texts.length) setText(t => [t, ...texts].filter(Boolean).join('\n').slice(0, 20000))
        if (pictures.length && !text.trim() && !texts.length) setText('Extract the scheduling request from these lecturer screenshots. Flag any missing information for review.')
        setImages(pictures); setFileNames(files.map(f => f.name))
      } catch { setError('That file could not be read. Use plain text or PNG/JPEG screenshots.') }
    }} /></label>
    {fileNames.length > 0 && <p className="text-[12px] break-all text-muted">{fileNames.join(', ')}</p>}
    <ErrorText>{error || job?.error}</ErrorText><Button type="submit" busy={busy || !!reading} disabled={!text.trim()}>{reading ? `AI ${job.status}…` : job?.status === 'failed' ? 'Retry with this message' : 'Create an editable proposal'}</Button>
    {reading && <Button type="button" variant="secondary" onClick={async () => { try { await api.del(`${base}/ai-jobs/${job.id}`); setJob({ ...job, status: 'cancelled' }); store.set(pollKey, '') } catch (e) { setError(errorText(e)) } }}>Cancel request</Button>}
    {job?.status === 'completed' && <section className="flex flex-col gap-3 border-t border-navy/20 pt-4"><h3 className="text-[18px] font-extrabold">Review the interpretation</h3><p className="text-[14px]">{job.result.explanation}</p>{job.result.missingFields.length > 0 && <p className="text-[13px] font-bold text-alert">Needs clarification: {job.result.missingFields.join(', ')}</p>}<dl className="grid grid-cols-2 gap-2 text-[13px]"><dt>Event</dt><dd>{job.result.kind}</dd><dt>Date</dt><dd>{job.result.date ?? 'Not specified'}</dd><dt>Time</dt><dd>{job.result.start ?? 'Not specified'}{job.result.end && `–${job.result.end}`}</dd><dt>Duration</dt><dd>{job.result.duration ? `${job.result.duration} minutes` : 'Not specified'}</dd><dt>Location</dt><dd>{job.result.room || 'Not specified'}</dd></dl><Button type="button" onClick={() => onUse(job.result)} variant="secondary">{job.result.intent === 'find-slots' ? 'Review slot-search settings' : 'Open proposal editor'}</Button></section>}
    <p className="text-[12px] text-muted">AI suggests details; it never publishes. Your message stays here if the service is unavailable.</p>
  </form>
}
