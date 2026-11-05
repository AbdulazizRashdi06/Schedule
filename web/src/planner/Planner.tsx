import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, Navigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { addDays, isoDate } from '../lib/slots'
import { yearName } from '../lib/year'
import { Button, ErrorText, Icon, Select, Sk, TextField, cx } from '../components/ui'
import { EventCard, MonthCalendar, WeekCalendar } from './Calendar'
import { AiAssistant, ProposalEditor, SlotFinder, TargetPicker } from './Panels'
import { campusToday, dateValue, emptySpec, type AiResult, type Board, type Catalog, type Entry, type Proposal, type SlotRequest, type Spec, type Target, type Workspace } from './types'

type Panel = 'drafts' | 'slots' | 'ai' | 'batches' | 'history' | 'settings'
type Editing = { spec: Spec; proposal?: Proposal; eventId?: number; baseEventVersion?: number }

export default function Planner() {
  const { session } = useSession()
  if (!session) return <div className="min-h-dvh bg-cream p-6" aria-busy="true"><Sk className="h-16 rounded-[18px]" /></div>
  if (session && session.role !== 'owner' && session.role !== 'batch-rep') return <Navigate to="/" replace />
  return <PlannerWorkspace />
}

function PlannerWorkspace() {
  const [connectionRetry, setConnectionRetry] = useState(0)
  const [catalog, setCatalog] = useState<Catalog | null>(null), [semesterId, setSemesterId] = useState<number | null>(null), [family, setFamily] = useState('')
  const [workspace, setWorkspace] = useState<Workspace | null>(null), [board, setBoard] = useState<Board | null>(null), [entries, setEntries] = useState<Entry[]>([])
  const [calendarLoading, setCalendarLoading] = useState(true)
  const [error, setError] = useState(''), [notice, setNotice] = useState(''), [view, setView] = useState<'week' | 'month' | 'semester'>('month'), [kind, setKind] = useState('both')
  const [anchor, setAnchor] = useState(campusToday()), [majorIds, setMajorIds] = useState<number[]>([]), [year, setYear] = useState(''), [batchIds, setBatchIds] = useState<number[]>([]), [explicitTargets, setExplicitTargets] = useState<Target[]>([])
  const [panel, setPanel] = useState<Panel>('drafts'), [editing, setEditing] = useState<Editing | null>(null), [editKey, setEditKey] = useState(0), [agenda, setAgenda] = useState<string | null>(null)
  const [slotInitial, setSlotInitial] = useState<Partial<SlotRequest> | undefined>(), [slotKey, setSlotKey] = useState(0)
  const side = useRef<HTMLElement>(null)
  const base = workspace ? `/rep/workspaces/${workspace.id}` : ''
  useEffect(() => {
    let active = true; setError(''); setWorkspace(null); setBoard(null); setEditing(null)
    api.get<Catalog>(`/rep/bootstrap${semesterId ? `?semesterId=${semesterId}` : ''}`).then(data => {
      if (!active) return
      setCatalog(data)
      setFamily(old => data.majors.some(m => m.family === old) ? old : data.assignment?.homeFamily ?? data.majors[0]?.family ?? '')
      setMajorIds([]); setBatchIds([]); setExplicitTargets([]); setYear('')
      if (semesterId && !data.semesters.find(s => s.id === data.semesterId)?.isCurrent && data.weeks?.week1Start) setAnchor(data.weeks.week1Start)
    }, e => { if (active) setError(errorText(e)) })
    return () => { active = false }
  }, [semesterId, connectionRetry])
  useEffect(() => {
    if (!catalog?.semesterId || !family) return
    let active = true; setWorkspace(null); setBoard(null); setEditing(null); setError('')
    api.post<Workspace>('/rep/workspaces/open', { semesterId: catalog.semesterId, family }).then(w => { if (active) setWorkspace(w) }, e => { if (active) setError(errorText(e)) })
    return () => { active = false }
  }, [catalog, family])
  const range = useMemo(() => {
    const d = dateValue(anchor)
    if (view === 'week') { const start = addDays(d, -d.getDay()); return { from: isoDate(start), to: isoDate(addDays(start, 6)) } }
    const first = new Date(d.getFullYear(), d.getMonth(), 1, 12), start = addDays(first, -first.getDay())
    const lastMonth = new Date(d.getFullYear(), d.getMonth() + (view === 'semester' ? 3 : 0), 1, 12)
    const lastStart = addDays(lastMonth, -lastMonth.getDay())
    return { from: isoDate(start), to: isoDate(addDays(lastStart, 41)) }
  }, [anchor, view])
  const refresh = useCallback(async () => {
    if (!base) return
    const [nextBoard, nextEntries] = await Promise.all([api.get<Board>(base), api.get<Entry[]>(`${base}/calendar?from=${range.from}&to=${range.to}`)])
    setBoard(nextBoard); setEntries(nextEntries)
  }, [base, range.from, range.to])
  useEffect(() => {
    if (!base) return
    setCalendarLoading(true)
    let active = true
    const update = () => Promise.all([api.get<Board>(base), api.get<Entry[]>(`${base}/calendar?from=${range.from}&to=${range.to}`)]).then(([b, e]) => { if (active) { setBoard(b); setEntries(e); setCalendarLoading(false) } }, e => { if (active) { setError(errorText(e)); setCalendarLoading(false) } })
    void update(); const timer = setInterval(() => { if (!document.hidden) void update() }, 15000)
    const focus = () => void update(); window.addEventListener('focus', focus)
    return () => { active = false; clearInterval(timer); window.removeEventListener('focus', focus) }
  }, [base, range.from, range.to])

  const courses = catalog?.courses ?? []
  const selectedFamilies = catalog?.majors.filter(m => majorIds.includes(m.id)).map(m => m.family) ?? []
  const selectedCourses = courses.filter(c => (!majorIds.length || !c.majors.length || c.majors.some(code => catalog?.majors.some(m => selectedFamilies.includes(m.family) && m.code === code))) && (!year || c.year == null || c.year === Number(year)))
  const targets = useMemo(() => {
    const chosenBatches = board?.batches.filter(b => batchIds.includes(b.id)) ?? []
    const batchTargets = chosenBatches.flatMap(b => b.targets)
    const workspaceTargets = board?.batches.flatMap(b => b.targets) ?? []
    const source = explicitTargets.length ? explicitTargets : batchIds.length ? batchTargets : !majorIds.length && !year && workspaceTargets.length ? workspaceTargets : selectedCourses.map(c => ({ courseId: c.id, sectionIds: [] }))
    const filtered = source.filter(t => selectedCourses.some(c => c.id === t.courseId))
    return [...new Set(filtered.map(t => t.courseId))].map(id => {
      const parts = filtered.filter(t => t.courseId === id)
      return { courseId: id, sectionIds: parts.some(t => t.sectionIds.length === 0) ? [] : [...new Set(parts.flatMap(t => t.sectionIds))] }
    })
  }, [board?.batches, batchIds, explicitTargets, selectedCourses, majorIds.length, year])
  const visible = entries.filter(e => (kind === 'both' || e.kind === kind) && e.targets.some(t => targets.some(s => s.courseId === t.courseId && (!t.sectionIds.length || !s.sectionIds.length || t.sectionIds.some(id => s.sectionIds.includes(id))))))

  const focusEditor = (value: Editing) => { setEditing(value); setEditKey(k => k + 1); setPanel('drafts'); setNotice(''); requestAnimationFrame(() => { if (window.innerWidth < 1100) side.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }) }) }
  const open = async (entry: Entry, moveTo?: string) => {
    setError('')
    try {
      if (entry.proposalId) {
        const p = board?.proposals.find(p => p.id === entry.proposalId)
        if (!p) { await refresh(); throw new Error('This draft changed. Open it again after refreshing.') }
        focusEditor({ proposal: p, spec: { ...p.spec, ...(moveTo ? { date: moveTo } : {}) } })
      } else if (entry.eventId) {
        const e = await api.get<{ id: number; version: number; spec: Spec }>(`${base}/events/${entry.eventId}`)
        focusEditor({ eventId: e.id, baseEventVersion: e.version, spec: { ...e.spec, ...(moveTo ? { date: moveTo } : {}) } })
      } else focusEditor({ spec: { ...emptySpec(moveTo ?? entry.date, entry.targets), kind: 'class', action: 'replace', title: entry.title, start: entry.start, end: entry.end, room: entry.room, source: entry.source } })
      if (moveTo) { setAnchor(moveTo); setNotice('Moved in a proposal only. Save and publish after reviewing conflicts.') }
    } catch (e) { setError(errorText(e)) }
  }
  const move = (key: string, date: string) => { const entry = entries.find(e => e.key === key); if (entry?.kind === 'exam') void open(entry, date) }
  const newProposal = () => focusEditor({ spec: emptySpec(agenda ?? anchor, targets.slice(0, 1)) })
  const useAi = async (result: AiResult) => {
    const batchTargets = board?.batches.filter(b => result.batchIds.includes(b.id)).flatMap(b => b.targets) ?? []
    const courseIds = [...new Set([...result.courseIds, ...batchTargets.map(t => t.courseId)])]
    const nextTargets = courseIds.map(courseId => ({ courseId, sectionIds: courses.find(c => c.id === courseId)?.sections.filter(s => result.sectionIds.includes(s.id)).map(s => s.id) ?? [] }))
    if (result.intent === 'find-slots') {
      if (nextTargets.length) setExplicitTargets(nextTargets)
      setMajorIds([]); setYear(''); setBatchIds([])
      setSlotInitial({ kind: result.kind, from: result.date ?? campusToday(), to: result.to ?? result.date ?? campusToday(), duration: result.duration ?? 120, room: result.room, repeatUntil: result.repeatUntil })
      setSlotKey(k => k + 1); setPanel('slots'); return
    }
    let source = entries.find(e => e.key === result.sourceKey)?.source ?? null
    if (!source && result.sourceKey) {
      try { const date = result.sourceKey.slice(-10); const context = await api.get<Entry[]>(`${base}/calendar?from=${date}&to=${date}`); source = context.find(e => e.key === result.sourceKey)?.source ?? null }
      catch (e) { setError(errorText(e)); return }
    }
    if (result.action !== 'add' && !source) { setError('Select the original university class in the calendar before making this change.'); return }
    const spec: Spec = { kind: result.kind, action: source ? result.action : 'add', title: result.title, date: result.date ?? '', start: result.start ?? '', end: result.end ?? '', room: result.room, targets: source ? [{ courseId: source.courseId, sectionIds: [source.sectionId] }] : nextTargets, repeatUntil: result.repeatUntil, source, notes: result.missingFields.length ? `Review: ${result.missingFields.join(', ')}` : '' }
    focusEditor({ spec })
  }
  const navigate = (direction: number) => {
    const d = dateValue(anchor)
    setAnchor(view === 'week' ? isoDate(addDays(d, 7 * direction)) : isoDate(new Date(d.getFullYear(), d.getMonth() + direction * (view === 'semester' ? 4 : 1), 1, 12)))
    setAgenda(null)
  }
  const saved = async () => { await refresh(); setNotice('Draft saved. Students still see the published schedule.') }
  const published = async () => { await refresh(); setNotice('Published. Affected students will see this event in their timetable.') }
  const families = [...new Set(catalog?.majors.map(m => m.family) ?? [])]
  return <div className="min-h-dvh bg-cream text-navy"><div className="mx-auto max-w-[1920px] px-4 pt-[max(1.5rem,env(safe-area-inset-top))] pb-[max(2rem,env(safe-area-inset-bottom))] min-[700px]:px-8">
    <header className="flex flex-wrap items-start justify-between gap-4 border-b-[1.5px] border-navy/20 pb-5"><div><Link to="/" className="label inline-flex min-h-11 items-center gap-2 text-[11px] link-hover"><Icon name="back" className="size-4" />Back to timetable</Link><h1 className="display text-[38px] leading-tight tracking-[-0.04em] min-[700px]:text-[52px]">Semester Planner.</h1><p className="mt-2 max-w-2xl text-[14px] font-semibold text-muted">One shared place for your family's classes, exams and scheduling proposals.</p></div>
      <div className="flex flex-wrap items-end gap-2">{catalog && <><label className="flex flex-col gap-1 text-[12px] font-bold">Semester<Select value={catalog.semesterId ?? ''} onChange={e => setSemesterId(Number(e.target.value))}>{catalog.semesters.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</Select></label>{catalog.isOwner && <label className="flex flex-col gap-1 text-[12px] font-bold">Family workspace<Select value={family} onChange={e => setFamily(e.target.value)}>{families.map(f => <option key={f} value={f}>{f.startsWith('family:') ? f.slice(7) : catalog.majors.find(m => m.family === f)?.code}</option>)}</Select></label>}</>}{board && <Button onClick={newProposal}>Add exam / class</Button>}</div>
    </header>
    <ErrorText>{error}</ErrorText>{error && <Button variant="secondary" onClick={() => { setError(''); if (base) void refresh().catch(e => setError(errorText(e))); else setConnectionRetry(n => n + 1) }}>Refresh planner</Button>}
    {notice && <p role="status" className="my-4 rounded-[14px] bg-ice px-4 py-3 text-[14px] font-semibold">{notice}</p>}
    {catalog?.isOwner && !catalog.enabled && <p className="my-4 rounded-[14px] border border-navy/20 p-4 text-[14px] font-bold">Owner preview: the planner is disabled for reps and students. Enable it under Workspace settings after checking the feature.</p>}
    {!board || !catalog ? <div aria-busy="true" aria-label="Loading semester planner" className="mt-6 grid gap-4 min-[1100px]:grid-cols-[1fr_350px]"><Sk className="h-[600px] rounded-[20px]" /><Sk className="h-[600px] rounded-[20px]" /></div> : <>
      <div className="grid min-w-0 gap-6 pt-6 min-[1100px]:grid-cols-[minmax(0,1fr)_350px]">
        <main className="min-w-0" aria-busy={calendarLoading}>
          <section aria-label="Calendar filters" className="mb-5 flex flex-col gap-3"><div className="flex flex-wrap gap-2"><details className="relative"><summary className="tint flex min-h-11 cursor-pointer items-center rounded-full border border-navy/20 px-4 text-[13px] font-bold">Majors {majorIds.length ? `(${majorIds.length})` : '(all)'}</summary><div className="absolute top-full left-0 z-30 mt-1 max-h-64 w-64 overflow-y-auto rounded-[14px] border border-navy/20 bg-paper p-3 shadow-lg">{catalog.majors.map(m => <label key={m.id} className="flex min-h-11 items-center gap-2 text-[13px] font-bold"><input type="checkbox" checked={majorIds.includes(m.id)} onChange={e => setMajorIds(ids => e.target.checked ? [...ids, m.id] : ids.filter(id => id !== m.id))} className="size-4 accent-navy" />{m.code} · {m.name}</label>)}</div></details>
            <label className="flex items-center gap-2 text-[13px] font-bold">Year<Select aria-label="Study year filter" value={year} onChange={e => setYear(e.target.value)}><option value="">All years</option>{[0, 1, 2, 3, 4, 5].map(y => <option key={y} value={y}>{yearName(y)}</option>)}</Select></label>
            <details className="relative"><summary className="tint flex min-h-11 cursor-pointer items-center rounded-full border border-navy/20 px-4 text-[13px] font-bold">Courses & sections {explicitTargets.length ? `(${explicitTargets.length})` : ''}</summary><div className="absolute top-full left-0 z-30 mt-1 w-[min(340px,85vw)] rounded-[14px] border border-navy/20 bg-cream p-3 shadow-lg"><TargetPicker courses={selectedCourses} targets={explicitTargets} onChange={setExplicitTargets} /><Button variant="ghost" onClick={() => setExplicitTargets([])}>Clear course filter</Button></div></details>
          </div><div className="flex flex-wrap gap-1.5" aria-label="Batch filters">{board.batches.length === 0 && <button type="button" onClick={() => setPanel('batches')} className="min-h-11 text-[13px] font-bold underline underline-offset-4">Add a batch to start with its course defaults</button>}{board.batches.map(b => <button key={b.id} type="button" aria-pressed={batchIds.includes(b.id)} onClick={() => setBatchIds(ids => ids.includes(b.id) ? ids.filter(id => id !== b.id) : [...ids, b.id])} className={cx('min-h-11 rounded-full px-3 text-[12px] font-bold', batchIds.includes(b.id) ? 'bg-navy text-cream' : 'border border-navy/20 tint')}>{b.name}</button>)}</div></section>
          <div className="mb-4 flex flex-wrap items-center justify-between gap-3"><div className="flex flex-wrap gap-2"><div role="group" aria-label="Calendar layout" className="flex rounded-full border border-navy/20 p-1">{(['week', 'month', 'semester'] as const).map(v => <button key={v} type="button" aria-pressed={view === v} onClick={() => setView(v)} className={cx('min-h-11 rounded-full px-3 text-[12px] font-bold', view === v ? 'bg-navy text-cream' : 'tint')}>{v === 'week' ? 'Weekly' : v === 'month' ? 'Monthly' : '4 months'}</button>)}</div><Select aria-label="Event type filter" value={kind} onChange={e => setKind(e.target.value)}><option value="both">Both</option><option value="exam">Exams</option><option value="class">Classes</option></Select></div><div className="flex items-center gap-1"><button type="button" aria-label="Previous calendar period" className="tint grid size-11 place-items-center rounded-full" onClick={() => navigate(-1)}><Icon name="prev" /></button><button type="button" onClick={() => setAnchor(campusToday())} className="tint min-h-11 rounded-full px-3 text-[13px] font-bold">Today</button><button type="button" aria-label="Next calendar period" className="tint grid size-11 place-items-center rounded-full" onClick={() => navigate(1)}><Icon name="next" /></button></div></div>
          <div className="mb-3 flex flex-wrap items-center justify-between gap-2"><p className="text-[14px] font-extrabold">{dateValue(anchor).toLocaleDateString('en', { month: 'long', year: 'numeric' })}{view === 'week' && ` · week of ${range.from}`}</p><p className="text-[12px] text-muted">{targets.length} courses · Oman time · Dashed = draft</p></div>
          {agenda && <section className="mb-4 rounded-[18px] border border-navy/20 p-4" aria-label="Day agenda"><div className="mb-3 flex items-center justify-between"><h2 className="text-[20px] font-extrabold">{dateValue(agenda).toLocaleDateString('en', { weekday: 'long', day: 'numeric', month: 'long' })}</h2><button type="button" onClick={() => setAgenda(null)} className="tint min-h-11 rounded-full px-3 text-[13px] font-bold">Close</button></div><div className="grid gap-2 min-[600px]:grid-cols-2">{visible.filter(e => e.date === agenda).map(e => <div key={e.key}><EventCard entry={e} courses={courses} onOpen={e => void open(e)} />{e.kind === 'exam' && <button type="button" className="tint min-h-11 px-2 text-[12px] font-bold" onClick={() => void open(e)}>Move exam / edit</button>}</div>)}</div>{!visible.some(e => e.date === agenda) && <p className="text-[14px] text-muted">No events match these filters.</p>}<Button className="mt-3" variant="secondary" onClick={newProposal}>Add event on this date</Button></section>}
          {calendarLoading ? <Sk className="h-[650px] rounded-[18px]" /> : view === 'week' ? <WeekCalendar entries={visible} courses={courses} date={anchor} onOpen={e => void open(e)} onDay={setAgenda} onMove={move} /> : <div className={cx('grid gap-5', view === 'semester' && 'min-[1900px]:grid-cols-2')}>{Array.from({ length: view === 'semester' ? 4 : 1 }, (_, i) => { const d = dateValue(anchor); const date = isoDate(new Date(d.getFullYear(), d.getMonth() + i, 1, 12)); return <MonthCalendar focusExams={kind !== 'class'} key={date} entries={visible} courses={courses} date={date} onOpen={e => void open(e)} onDay={setAgenda} onMove={move} /> })}</div>}
        </main>
        <aside ref={side} className="min-w-0 scroll-mt-5 border-t border-navy/20 pt-5 min-[1100px]:border-t-0 min-[1100px]:border-l min-[1100px]:pl-5 min-[1100px]:pt-0" aria-label="Workspace tools">
          <div className="mb-5 flex flex-wrap gap-1" role="group" aria-label="Workspace tool">{(['drafts', 'slots', 'ai', 'batches', 'history', 'settings'] as Panel[]).map(p => <button key={p} type="button" aria-pressed={panel === p} onClick={() => setPanel(p)} className={cx('min-h-11 rounded-full px-3 text-[12px] font-bold', panel === p ? 'bg-navy text-cream' : 'tint')}>{({ drafts: 'Proposals', slots: 'Find a slot', ai: 'AI assistant', batches: 'Batches', history: 'History', settings: 'Settings' })[p]}</button>)}</div>
          <div hidden={panel !== 'drafts'}>{editing ? <ProposalEditor key={editKey} initial={editing} courses={courses} base={base} onSaved={saved} onPublished={published} onClose={() => setEditing(null)} onWithdraw={editing.eventId ? () => setPanel('settings') : undefined} /> : <><h2 className="text-[24px] font-extrabold">Shared proposals</h2><p className="my-3 text-[13px] text-muted">Every rep in this family can review and edit these drafts. Published events stay unchanged until approval.</p>{board.proposals.length === 0 && <p className="mb-4 text-[14px] font-semibold">No drafts yet. Add an exam, choose a free slot, or paste a lecturer's message.</p>}<Button onClick={newProposal}>New proposal</Button><ul className="mt-4">{board.proposals.map(p => <li key={p.id} className="border-t border-navy/20 py-3"><button type="button" className="tint flex min-h-11 w-full flex-col rounded-lg p-2 text-left" onClick={() => focusEditor({ spec: p.spec, proposal: p })}><span className="font-extrabold">{p.spec.title}</span><span className="text-[13px]">{p.spec.date} · {p.spec.start}–{p.spec.end}</span><span className="text-[12px] text-muted">Draft · {p.updatedBy}</span></button></li>)}</ul></>}</div>
          <div hidden={panel !== 'slots'}><SlotFinder key={`${base}:${slotKey}`} base={base} targets={targets} initial={slotInitial} onPick={spec => { setAnchor(spec.date); focusEditor({ spec }) }} /></div>
          <div hidden={panel !== 'ai'}><AiAssistant key={base} base={base} onUse={useAi} /></div>
          <div hidden={panel !== 'batches'}><BatchPanel key={base} base={base} board={board} catalog={catalog} onChanged={refresh} /></div>
          <div hidden={panel !== 'history'}><h2 className="text-[24px] font-extrabold">Activity history</h2>{board.history.length === 0 && <p className="mt-3 text-[14px] text-muted">Published changes and batch edits appear here.</p>}<ol className="mt-3 flex flex-col gap-3">{board.history.map(a => <li key={a.id} className="border-t border-navy/20 pt-3 text-[13px]"><p className="font-extrabold">{a.action.split('-').join(' ')} · {a.actor}</p><p>{new Date(a.at).toLocaleString('en', { timeZone: 'Asia/Muscat' })}</p>{a.explanation && <p className="mt-1">{a.explanation}</p>}{a.action === 'published' && a.eventId && <button type="button" onClick={async () => { try { const current = await api.get<{ version: number }>(`${base}/events/${a.eventId}`); focusEditor({ spec: JSON.parse(a.snapshotJson) as Spec, eventId: a.eventId!, baseEventVersion: current.version }) } catch (e) { setError(errorText(e)) } }} className="tint min-h-11 font-bold underline underline-offset-4">Propose restoring this version</button>}</li>)}</ol></div>
          <div hidden={panel !== 'settings'}><WorkspaceSettings key={`${base}:${board.workspace.version}`} base={base} board={board} catalog={catalog} onChanged={refresh} onEnabled={value => setCatalog(c => c ? { ...c, enabled: value } : c)} />{editing?.eventId && <Withdraw base={base} eventId={editing.eventId} onChanged={async () => { setEditing(null); await refresh() }} />}</div>
        </aside>
      </div>
    </>}
  </div></div>
}

function BatchPanel({ base, board, catalog, onChanged }: { base: string; board: Board; catalog: Catalog; onChanged: () => Promise<void> }) {
  const [id, setId] = useState<number | null>(null), [name, setName] = useState(''), [major, setMajor] = useState(catalog.majors[0]?.id ?? 0), [year, setYear] = useState(1), [targets, setTargets] = useState<Target[]>([]), [version, setVersion] = useState(0)
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const reset = () => { setId(null); setName(''); setTargets([]); setVersion(0) }
  const save = async (prefill: boolean) => { setBusy(true); setError(''); try { const body = { name: name.trim() || `${catalog.majors.find(m => m.id === major)?.code} ${yearName(year)}`, targets, majorId: major, year, version, prefill }; if (id) await api.put(`${base}/batches/${id}`, body); else await api.post(`${base}/batches`, body); await onChanged(); reset() } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }
  return <div className="flex flex-col gap-4"><h2 className="text-[24px] font-extrabold">Shared batches</h2><p className="text-[13px] text-muted">Add any major to this family's workspace. A batch is a course selection, not a fixed student roster.</p>{board.batches.map(b => <div key={b.id} className="flex items-center justify-between gap-2 border-b border-navy/20 py-2"><span className="text-[14px] font-bold">{b.name} · {b.targets.length} courses</span><button type="button" onClick={() => { setId(b.id); setName(b.name); setMajor(b.majorId ?? major); setYear(b.year ?? 1); setTargets(b.targets); setVersion(b.version); setError('') }} className="tint min-h-11 rounded-full px-3 text-[13px] font-bold">Edit</button></div>)}
    <h3 className="text-[18px] font-extrabold">{id ? 'Edit batch' : 'Add a batch'}</h3><TextField label="Batch name" maxLength={80} value={name} onChange={e => setName(e.target.value)} placeholder="AI Year 2" /><label className="flex flex-col gap-1 text-[13px] font-bold">Major<Select value={major} onChange={e => setMajor(Number(e.target.value))}>{catalog.majors.map(m => <option key={m.id} value={m.id}>{m.code} · {m.name}</option>)}</Select></label><label className="flex flex-col gap-1 text-[13px] font-bold">Study year<Select value={year} onChange={e => setYear(Number(e.target.value))}>{[0, 1, 2, 3, 4, 5].map(y => <option key={y} value={y}>{yearName(y)}</option>)}</Select></label>
    {!id && <Button variant="secondary" busy={busy} onClick={() => save(true)}>Create with course defaults</Button>}
    <TargetPicker courses={catalog.courses} targets={targets} onChange={setTargets} /><ErrorText>{error}</ErrorText><Button busy={busy} onClick={() => save(false)}>{id ? 'Save batch' : 'Create with selected courses'}</Button>{id && <><Button variant="ghost" onClick={reset}>Cancel editing</Button><Button variant="danger" disabled={busy} onClick={async () => { setBusy(true); try { await api.del(`${base}/batches/${id}?version=${version}`); await onChanged(); reset() } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>Remove batch selection</Button></>}
  </div>
}

function WorkspaceSettings({ base, board, catalog, onChanged, onEnabled }: { base: string; board: Board; catalog: Catalog; onChanged: () => Promise<void>; onEnabled: (value: boolean) => void }) {
  const [daily, setDaily] = useState(board.workspace.dailyExamWarning), [weekly, setWeekly] = useState(board.workspace.weeklyExamWarning), [busy, setBusy] = useState(false), [error, setError] = useState('')
  return <div className="flex flex-col gap-4"><h2 className="text-[24px] font-extrabold">Workspace settings</h2><p className="text-[13px] text-muted">Family workspace: {board.workspace.family.replace('family:', '').replace('major:', 'Major ')}</p><TextField label="Warn at exams per day" type="number" min={1} max={10} value={daily} onChange={e => setDaily(Number(e.target.value))} /><TextField label="Warn at exams in seven days" type="number" min={1} max={20} value={weekly} onChange={e => setWeekly(Number(e.target.value))} /><ErrorText>{error}</ErrorText><Button busy={busy} onClick={async () => { setBusy(true); setError(''); try { await api.put(`${base}/settings`, { version: board.workspace.version, dailyExamWarning: daily, weeklyExamWarning: weekly }); await onChanged() } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>Save warning thresholds</Button>{catalog.isOwner && <label className="flex min-h-12 items-center gap-3 border-t border-navy/20 pt-4 text-[14px] font-extrabold"><input type="checkbox" checked={catalog.enabled} disabled={busy} className="size-5 accent-navy" onChange={async e => { const value = e.target.checked; setBusy(true); try { await api.put('/admin/planner/settings', { enabled: value }); onEnabled(value) } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }} />Enable planner for assigned reps and students</label>}</div>
}

function Withdraw({ base, eventId, onChanged }: { base: string; eventId: number; onChanged: () => Promise<void> }) {
  const [explanation, setExplanation] = useState(''), [busy, setBusy] = useState(false), [error, setError] = useState('')
  return <section className="mt-6 flex flex-col gap-3 border-t border-navy/20 pt-4"><h3 className="text-[18px] font-extrabold">Withdraw selected event</h3><p className="text-[13px] text-muted">Removes this published event from student timetables. Withdrawing a class change restores the university occurrence.</p><TextField label="Reason for withdrawal" value={explanation} maxLength={1000} onChange={e => setExplanation(e.target.value)} /><ErrorText>{error}</ErrorText><Button variant="danger" busy={busy} disabled={!explanation.trim()} onClick={async () => { setBusy(true); try { const e = await api.get<{ version: number }>(`${base}/events/${eventId}`); await api.post(`${base}/events/${eventId}/withdraw`, { version: e.version, explanation }); await onChanged() } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>Withdraw from student timetables</Button></section>
}
