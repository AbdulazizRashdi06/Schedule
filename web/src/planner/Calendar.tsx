import type { DragEvent } from 'react'
import type { Course } from '../lib/types'
import { addDays, isoDate } from '../lib/slots'
import { cx } from '../components/ui'
import { courseColour, dateValue, type Entry } from './types'

const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']
const min = (v: string) => Number(v.slice(0, 2)) * 60 + Number(v.slice(3))

export function EventCard({ entry: e, courses, onOpen, compact = false, condensed = false }: { entry: Entry; courses: Course[]; onOpen: (e: Entry) => void; compact?: boolean; condensed?: boolean }) {
  const codes = e.targets.map(t => courses.find(c => c.id === t.courseId)?.code ?? 'Course').join(' / ')
  const draft = e.status === 'draft' || e.status === 'cancelled-draft'
  return <button type="button" draggable={e.kind === 'exam'} onDragStart={(event) => { event.dataTransfer.setData('application/classy-event', e.key); event.dataTransfer.effectAllowed = 'move' }} onClick={() => onOpen(e)}
    title={`${codes}: ${e.title}, ${e.date} ${e.start}–${e.end}, ${e.room || 'Location not set'}, ${e.status}`}
    className={cx('press flex min-h-11 w-full min-w-0 flex-col rounded-[10px] p-2 text-left leading-snug outline-1 -outline-offset-1 outline-navy/20 focus-visible:outline-3', courseColour(e.targets[0]?.courseId ?? 0), draft && 'outline-2 outline-dashed', e.stale && 'outline-alert', compact ? 'text-[11px]' : 'text-[13px]')}>
    {condensed ? <><span className="font-extrabold">{codes}</span><span>{e.start}–<br />{e.end}</span>{e.room && <span>{e.room}</span>}</> : <><span className="font-extrabold">{e.kind === 'exam' ? 'EXAM · ' : ''}{e.start}–{e.end}</span><span className="font-bold break-words">{e.title}</span><span className="break-words">{codes}{e.room && ` · ${e.room}`}</span></>}
    {(draft || e.stale) && <span className="mt-1 font-extrabold">{e.stale ? 'Source changed · review' : e.status === 'cancelled-draft' ? 'Draft cancellation' : 'Draft'}</span>}
  </button>
}

interface Props { entries: Entry[]; courses: Course[]; date: string; onOpen: (e: Entry) => void; onDay: (date: string) => void; onMove: (key: string, date: string) => void; focusExams?: boolean }
function drop(event: DragEvent, date: string, move: Props['onMove']) {
  event.preventDefault()
  const key = event.dataTransfer.getData('application/classy-event')
  if (key) move(key, date)
}

export function WeekCalendar({ entries, courses, date, onOpen, onDay, onMove }: Props) {
  const current = dateValue(date), start = addDays(current, -current.getDay())
  const firstHour = Math.min(8, ...entries.map(e => Math.floor(min(e.start) / 60)))
  const lastHour = Math.max(19, ...entries.map(e => Math.ceil(min(e.end) / 60)))
  const hours = Array.from({ length: lastHour - firstHour }, (_, i) => i + firstHour)
  return <div className="overflow-x-auto rounded-[18px] border border-navy/20 bg-paper" aria-label="Weekly planner">
    <div className="grid min-w-[780px] grid-cols-[48px_repeat(7,minmax(0,1fr))]">
      <div className="border-b border-navy/15" />
      {days.map((name, i) => { const iso = isoDate(addDays(start, i)); return <div key={iso} onDragOver={e => e.preventDefault()} onDrop={e => drop(e, iso, onMove)} className="border-b border-l border-navy/15 p-2">
        <button type="button" onClick={() => onDay(iso)} className="flex min-h-11 w-full items-center justify-between gap-1 rounded-lg px-1 font-extrabold tint"><span>{name}</span><span>{dateValue(iso).getDate()}</span></button>
        <div className="flex flex-col gap-1 pb-1">{entries.filter(e => e.date === iso && e.kind === 'exam').map(e => <EventCard key={e.key} entry={e} courses={courses} onOpen={onOpen} compact />)}</div>
      </div> })}
      <div className="relative" style={{ height: hours.length * 64 }}>{hours.map(h => <span key={h} className="absolute right-1 text-[11px] font-bold text-muted" style={{ top: (h - firstHour) * 64 + 4 }}>{String(h).padStart(2, '0')}:00</span>)}</div>
      {days.map((_, i) => {
        const iso = isoDate(addDays(start, i)); const events = entries.filter(e => e.date === iso && e.kind === 'class')
        return <div key={iso} className="relative border-l border-navy/15" style={{ height: hours.length * 64 }} onDragOver={e => e.preventDefault()} onDrop={e => drop(e, iso, onMove)}>
          {hours.map(h => <div key={h} className="pointer-events-none absolute inset-x-0 border-t border-navy/10" style={{ top: (h - firstHour) * 64 }} />)}
          {events.map(e => {
            const overlaps = events.filter(x => x.start < e.end && e.start < x.end).sort((a, b) => a.key.localeCompare(b.key))
            const lane = overlaps.findIndex(x => x.key === e.key)
            return <div key={e.key} className="absolute overflow-y-auto px-0.5" style={{ top: (min(e.start) - firstHour * 60) * 64 / 60, height: Math.max(44, (min(e.end) - min(e.start)) * 64 / 60), left: `${lane * 100 / overlaps.length}%`, width: `${100 / overlaps.length}%` }}><EventCard entry={e} courses={courses} onOpen={onOpen} compact condensed /></div>
          })}
        </div>
      })}
    </div>
  </div>
}

export function MonthCalendar({ entries, courses, date, onOpen, onDay, onMove, focusExams = true }: Props) {
  const d = dateValue(date), first = new Date(d.getFullYear(), d.getMonth(), 1, 12)
  const start = addDays(first, -first.getDay())
  return <div className="min-w-0 overflow-x-auto"><section className="min-w-[700px] overflow-hidden rounded-[18px] border border-navy/20 bg-paper" aria-label={first.toLocaleDateString('en', { month: 'long', year: 'numeric' })}>
    <h2 className="px-4 py-3 text-[20px] font-extrabold">{first.toLocaleDateString('en', { month: 'long', year: 'numeric' })}</h2>
    <div className="grid grid-cols-7">{days.map(day => <span key={day} className="border-y border-navy/15 py-2 text-center text-[11px] font-bold text-muted">{day}</span>)}
      {Array.from({ length: 42 }, (_, i) => {
        const day = addDays(start, i), iso = isoDate(day), here = entries.filter(e => e.date === iso).sort((a, b) => Number(b.kind === 'exam') - Number(a.kind === 'exam') || a.start.localeCompare(b.start))
        const shown = focusExams ? here.filter(e => e.kind === 'exam') : here
        return <div key={iso} className={cx('min-h-[108px] min-w-0 border-r border-b border-navy/10 p-1', day.getMonth() !== first.getMonth() && 'bg-navy/[0.035]')} onDragOver={e => e.preventDefault()} onDrop={e => drop(e, iso, onMove)}>
          <button type="button" aria-label={`Open ${iso}`} onClick={() => onDay(iso)} className="tint flex min-h-11 w-full items-center justify-center rounded-lg text-[13px] font-bold">{day.getDate()}</button>
          <div className="flex flex-col gap-1">{shown.slice(0, 3).map(e => <EventCard key={e.key} entry={e} courses={courses} onOpen={onOpen} compact />)}</div>
          {here.length > 0 && <button type="button" onClick={() => onDay(iso)} className="tint mt-1 min-h-11 w-full rounded-lg text-[11px] font-bold">{here.length} {here.length === 1 ? 'event' : 'events'} · View</button>}
        </div>
      })}
    </div>
  </section></div>
}
