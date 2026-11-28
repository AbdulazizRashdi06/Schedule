import { useEffect, useMemo, useRef, useState } from 'react'
import { api, errorText } from '../lib/api'
import { SLOTS, blockOn, clock as fmtClock, fmtDay, isoDate, sessionCode, type Session } from '../lib/slots'
import { fmtRange, fmtTime, minutes } from '../lib/time'
import type { ChangeBody, ChangePreview, ClassChange, Timetable } from '../lib/types'
import { useHome } from '../lib/viewer'
import { Button, ErrorText, Icon, Notice, Sheet, TextField, cx } from '../components/ui'
import { Clock, TimeButton } from '../student/SlotSheet'
import { useFreshCode } from './FreshCode'

/**
 * What the sheet is asked to do. For a cancel, move or room change, `s` is the session on `date`: as the university
 * lists it, or one that already has a change (greyed, moved, re-roomed), which is then edited rather than made anew.
 * For an extra class: the day and time tapped, the classes to tick, or the extra class to edit.
 */
export type ChangeRequest =
  | { kind: 'cancel' | 'move' | 'room'; s: Session; date: Date }
  | { kind: 'extra'; date?: string; start?: string; end?: string; ticked?: number[]; edit?: Session }

/** What happened, for the page that asked: the words for the toast, the changes made, or the one that was undone. */
export interface ChangeDone {
  text: string
  changes: ClassChange[]
  undone?: ClassChange
}

/** Undoes changes, one request per group (one real class under several codes is one group). */
export async function undoChanges(changes: ClassChange[]) {
  for (const group of new Set(changes.map((c) => c.group))) await api.post(`/teach/changes/${group}/undo`)
}

const TYPES = [
  ['lecture', 'Lecture'],
  ['lab', 'Lab'],
  ['tutorial', 'Tutorial'],
] as const
const NOTE_MAX = 200
const chip = (on: boolean) => cx('press label h-10 shrink-0 rounded-full px-4 text-[12px]', on ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_color-mix(in_srgb,var(--color-navy)_20%,transparent)]')
const sameRoom = (a: string, b: string) => a.replace(/\s+/g, '').toLowerCase() === b.replace(/\s+/g, '').toLowerCase()

/** A tick box with its text, as one control. */
function Tick({ checked, onChange, children, disabled }: { checked: boolean; onChange: (v: boolean) => void; children: React.ReactNode; disabled?: boolean }) {
  return (
    <button type="button" role="checkbox" aria-checked={checked} aria-disabled={disabled || undefined} onClick={() => !disabled && onChange(!checked)} className="press flex min-h-11 items-center gap-3 py-1 text-left">
      <span aria-hidden="true" className={cx('grid size-[26px] shrink-0 place-items-center rounded-[8px]', checked ? 'bg-navy text-cream' : 'border-2 border-navy/35')}>
        {checked && <Icon name="check" className="pop size-4" strokeWidth={3.4} />}
      </span>
      <span className="min-w-0 text-[16px] leading-tight font-bold">{children}</span>
    </button>
  )
}

/**
 * The classes an extra class can be for: every class on the teacher's timetable, with one real class under several
 * course codes as one row.
 */
function classRows(tt: Timetable | null, sessions: Session[]) {
  const groups: number[][] = []
  const add = (ids: number[]) => {
    const joined = groups.filter((g) => g.some((id) => ids.includes(id)))
    for (const g of joined) groups.splice(groups.indexOf(g), 1)
    groups.push([...new Set([...joined.flat(), ...ids])])
  }
  for (const s of sessions) if (!s.mine && !s.office && !s.plannerEventId) add((s.merged ?? [s]).map((m) => m.cls.section.id))
  for (const c of tt?.classes ?? []) add([c.section.id])
  return groups
    .map((ids) => {
      const classes = (tt?.classes ?? []).filter((c) => ids.includes(c.section.id)).sort((a, b) => (a.code < b.code ? -1 : a.code > b.code ? 1 : 0))
      return { ids: classes.map((c) => c.section.id), classes }
    })
    .filter((r) => r.classes.length > 0)
    .sort((a, b) => (a.classes[0].code < b.classes[0].code ? -1 : a.classes[0].code > b.classes[0].code ? 1 : a.classes[0].section.number.localeCompare(b.classes[0].section.number)))
}

/** The change forms: cancel, move, another room, an extra class. One sheet, one screen per kind. */
export default function ChangeSheet({ request, onClose, onDone }: { request: ChangeRequest | null; onClose: () => void; onDone: (done: ChangeDone) => void }) {
  const label = !request ? 'Change' : request.kind === 'cancel' ? 'Cancel class' : request.kind === 'move' ? 'Move class' : request.kind === 'room' ? 'Change room' : 'Extra class'
  return (
    <Sheet open={!!request} onClose={onClose} label={label}>
      {request && <ChangeForm request={request} onClose={onClose} onDone={onDone} />}
    </Sheet>
  )
}

function ChangeForm({ request, onClose, onDone }: { request: ChangeRequest; onClose: () => void; onDone: (done: ChangeDone) => void }) {
  const { tt, sessions } = useHome()
  const fresh = useFreshCode()
  const kind = request.kind
  const s = request.kind === 'extra' ? request.edit : request.s
  /** The live change on this session, which this form then replaces. */
  const replacing = s?.change
  // "Change room" on a class that was moved stays a move, at the same new time, with another room.
  const sendKind = kind === 'room' && replacing?.kind === 'move' ? 'move' : kind
  const picksTime = kind === 'move' || kind === 'extra'
  const picksRoom = kind !== 'cancel'

  // The session as the university lists it: the lead of the codes that meet that day.
  const lead = request.kind === 'extra' || !s ? undefined : (blockOn(s, request.date)[0] ?? s)
  const sessionDate = request.kind === 'extra' ? '' : isoDate(request.date)
  /** Where the class is now: its room, and for a class already moved or added, its time. */
  const nowAt = replacing?.to ?? (s && request.kind !== 'extra' ? { date: sessionDate, start: s.meeting.start, end: s.meeting.end, room: s.meeting.room } : null)
  const length = s ? Math.max(15, s.end - s.start) : 90

  const keepsTime = !!replacing?.to && (replacing.kind === 'move' || replacing.kind === 'extra') && (kind === 'extra' || replacing.kind === 'move')
  const [newDate, setNewDate] = useState(keepsTime ? replacing!.to!.date : request.kind === 'extra' ? (request.date ?? '') : sessionDate)
  const [start, setStart] = useState(keepsTime ? replacing!.to!.start : request.kind === 'extra' ? (request.start ?? '') : '')
  const [end, setEnd] = useState(keepsTime ? replacing!.to!.end : request.kind === 'extra' ? (request.end ?? '') : '')
  const [custom, setCustom] = useState(() => !!start && !SLOTS.some(([a]) => a === start))
  const [openClock, setOpenClock] = useState<'start' | 'end' | null>(null)
  // null: no room chosen yet. '' is "to be announced".
  const [room, setRoom] = useState<string | null>(kind === 'move' ? (nowAt?.room ?? '') : kind === 'extra' ? (replacing?.to?.room ?? '') : null)
  const [typing, setTyping] = useState(false)
  const [type, setType] = useState<(typeof TYPES)[number][0]>(replacing?.kind === 'extra' ? replacing.type : 'lecture')
  const [note, setNote] = useState(replacing?.note ?? '')
  const [alsoOther, setAlsoOther] = useState(false)
  /** Course codes of the class left out of this change (none: all of them). */
  const [left, setLeft] = useState<number[]>([])
  /** An extra class: the sections it is for. */
  const [ticked, setTicked] = useState<number[]>(request.kind === 'extra' ? (request.edit ? (request.edit.merged ?? [request.edit]).map((m) => m.cls.section.id) : (request.ticked ?? [])) : [])
  const [preview, setPreview] = useState<ChangePreview | null>(null)
  const [previewError, setPreviewError] = useState('')
  const [free, setFree] = useState<string[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const rows = useMemo(() => (kind === 'extra' && !replacing ? classRows(tt, sessions) : []), [kind, replacing, tt, sessions])
  const tickable = (preview?.codes ?? []).filter((c) => !c.changedBy)
  const timed = !picksTime || (!!newDate && !!start && !!end)

  const body: ChangeBody = {
    kind: sendKind,
    // The session is named for a new change; a change being edited is named by its group.
    ...(!replacing && lead && { sectionId: lead.cls.section.id, date: sessionDate, start: lead.meeting.start }),
    ...(kind === 'extra' ? { sectionIds: ticked } : tickable.length > 1 && left.length > 0 ? { sectionIds: tickable.map((c) => c.sectionId).filter((id) => !left.includes(id)) } : {}),
    ...(alsoOther && { alsoOther: true }),
    ...(sendKind === 'move' && kind === 'room' && replacing?.to ? { newDate: replacing.to.date, newStart: replacing.to.start, newEnd: replacing.to.end } : picksTime && timed ? { newDate, newStart: start, newEnd: end } : {}),
    ...(picksRoom && { room: room ?? '' }),
    ...(kind === 'extra' && { type }),
    note: note.trim(),
  }
  const sent = JSON.stringify(body)

  // Numbers and warnings from the server, a moment after the last input. They never stop the button.
  const asked = useRef(0)
  const first = useRef(true)
  useEffect(() => {
    const n = ++asked.current
    const t = setTimeout(
      () => {
        api.post<ChangePreview>('/teach/changes/preview', { ...(JSON.parse(sent) as ChangeBody), groupId: replacing?.group ?? null }).then(
          (p) => {
            if (n !== asked.current) return
            setPreview(p)
            setPreviewError('')
          },
          (e) => n === asked.current && setPreviewError(errorText(e)),
        )
      },
      first.current ? 0 : 400,
    )
    first.current = false
    return () => clearTimeout(t)
  }, [sent, replacing?.group])

  // The rooms free for the whole class at that time, leaving out the room this class takes itself.
  const at = kind === 'room' ? nowAt : picksTime && timed ? { date: newDate, start, end } : null
  const roomsQuery = !picksRoom || !at || at.end <= at.start
    ? null
    : `/teach/rooms?date=${at.date}&start=${at.start}&end=${at.end}` +
      (replacing?.kind === 'extra' ? `&groupId=${replacing.group}` : replacing?.from ? `&sectionId=${replacing.sectionId}&origDate=${replacing.from.date}&origStart=${replacing.from.start}` : lead ? `&sectionId=${lead.cls.section.id}&origDate=${sessionDate}&origStart=${lead.meeting.start}` : '')
  useEffect(() => {
    if (!roomsQuery) return setFree(null)
    let live = true
    api.get<{ rooms: { room: string }[] }>(roomsQuery).then(
      (r) => live && setFree(r.rooms.map((x) => x.room)),
      () => live && setFree([]),
    )
    return () => {
      live = false
    }
  }, [roomsQuery])

  const pickSlot = (from: string, to: string) => {
    setCustom(false)
    setOpenClock(null)
    setStart(from)
    // A moved class keeps its length; an extra class is one slot long.
    setEnd(kind === 'move' ? fmtClock(Math.min(minutes(from) + length, 23 * 60 + 59)) : to)
  }
  const changeStart = (v: string) => {
    const len = start && end && end > start ? minutes(end) - minutes(start) : length
    setStart(v)
    setEnd(fmtClock(Math.min(minutes(v) + len, 23 * 60 + 59)))
  }

  const blocked = preview?.blocked === 'started' ? 'This class has already started.' : preview?.blocked === 'switched-off' ? 'Class changes are switched off for now.' : ''
  const missing = kind === 'extra' && ticked.length === 0 ? 'Tick the classes this is for.' : !timed ? 'Pick a date and a time.' : picksRoom && room === null ? 'Pick a room.' : ''
  const can = !busy && !blocked && !missing

  const title = kind === 'cancel' ? 'Cancel class' : kind === 'move' ? (replacing?.kind === 'move' ? 'Move again' : 'Move class') : kind === 'room' ? 'Change room' : replacing ? 'Edit extra class' : 'Extra class'
  const action = kind === 'cancel' ? 'Cancel class' : kind === 'move' ? 'Move class' : kind === 'room' ? 'Change room' : replacing ? 'Save extra class' : 'Add extra class'
  const done = kind === 'cancel' ? 'Cancelled.' : kind === 'move' ? 'Moved.' : kind === 'room' ? 'Room changed.' : replacing ? 'Extra class saved.' : 'Extra class added.'

  const submit = async () => {
    if (!can) return setError(blocked || missing)
    setBusy(true)
    setError('')
    try {
      const r = await fresh.run(() => (replacing ? api.put<{ changes: ClassChange[] }>(`/teach/changes/${replacing.group}`, body) : api.post<{ changes: ClassChange[] }>('/teach/changes', body)))
      onClose()
      onDone({ text: `${done} Students with alerts on were told.`, changes: r.changes })
    } catch (e) {
      if (!fresh.cancelled(e)) setError(errorText(e))
      setBusy(false)
    }
  }

  const current = nowAt?.room ?? ''
  const others = preview?.others ?? []
  const picked = (r: string) => room !== null && !typing && sameRoom(room, r) && !!r

  return (
    <form
      className="flex flex-col gap-4 pt-1"
      onSubmit={(e) => {
        e.preventDefault()
        void submit()
      }}
    >
      <div className="flex flex-col gap-1.5">
        <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">{title}</h2>
        {s && nowAt && request.kind !== 'extra' && (
          <p className="text-[15px] leading-snug font-semibold text-muted">
            {sessionCode(s, request.date)} · {fmtDay(s.off ? sessionDate : nowAt.date)} {fmtRange(s.off ? s.meeting.start : nowAt.start, s.off ? s.meeting.end : nowAt.end)}
            {(s.off ? s.meeting.room : nowAt.room) ? ` · ${s.off ? s.meeting.room : nowAt.room}` : ''}
          </p>
        )}
        {kind === 'extra' && <p className="text-[15px] leading-snug font-semibold text-muted">A class on one date. It shows on your students’ timetables and they are told.</p>}
      </div>

      {kind !== 'extra' && (preview?.codes.length ?? 0) > 1 && (
        <fieldset className="flex flex-col">
          <legend className="label mb-1 text-[11px] tracking-[0.12em]">For which codes</legend>
          {preview!.codes.map((c) =>
            c.changedBy ? (
              <p key={c.sectionId} className="flex min-h-11 items-center gap-3 py-1 text-[16px] leading-tight font-bold text-navy/45">
                <span aria-hidden="true" className="size-[26px] shrink-0 rounded-[8px] bg-navy/10" />
                <span>
                  {c.code} <span className="label ml-1 text-[10px]">already changed</span>
                </span>
              </p>
            ) : (
              <Tick
                key={c.sectionId}
                checked={!left.includes(c.sectionId)}
                // At least one code stays.
                disabled={!left.includes(c.sectionId) && tickable.length - left.length <= 1}
                onChange={(on) => setLeft((l) => (on ? l.filter((id) => id !== c.sectionId) : [...l, c.sectionId]))}
              >
                {c.code}
                <span className="ml-2 font-mono text-[12px] font-bold text-muted uppercase">
                  sec {c.number} · {c.students} {c.students === 1 ? 'user' : 'users'}
                </span>
              </Tick>
            ),
          )}
        </fieldset>
      )}

      {kind === 'extra' && !replacing && (
        <fieldset className="flex flex-col">
          <legend className="label mb-1 text-[11px] tracking-[0.12em]">For which classes</legend>
          {rows.length === 0 && <p className="text-[15px] font-semibold text-muted">No classes on your timetable yet. Add them under Edit my classes.</p>}
          <div className={cx('flex flex-col', rows.length > 5 && 'max-h-[232px] overflow-y-auto rounded-[14px] px-3 shadow-[inset_0_0_0_2px_color-mix(in_srgb,var(--color-navy)_12%,transparent)]')}>
            {rows.map((r) => (
              <Tick
                key={r.ids.join('-')}
                checked={r.ids.every((id) => ticked.includes(id))}
                onChange={(on) => setTicked((t) => (on ? [...new Set([...t, ...r.ids])] : t.filter((id) => !r.ids.includes(id))))}
              >
                {[...new Set(r.classes.map((c) => c.code))].join(' · ')}
                {r.classes.length === 1 && r.classes[0].section.number && <span className="ml-2 font-mono text-[12px] font-bold text-muted uppercase">sec {r.classes[0].section.number}</span>}
                <span className="block text-[13px] font-semibold text-muted">{r.classes[0].name}</span>
              </Tick>
            ))}
          </div>
        </fieldset>
      )}

      {others.length > 0 && !replacing && (
        <Tick checked={alsoOther} onChange={setAlsoOther}>
          {others.length === 1 ? `Also the ${fmtTime(others[0].start)} session that day` : `Also the other ${others.length} sessions that day`}
        </Tick>
      )}

      {picksTime && (
        <div className="flex flex-col gap-2.5">
          <TextField label={kind === 'move' ? 'New date' : 'Date'} type="date" value={newDate} onChange={(e) => setNewDate(e.target.value)} />
          <div className="flex flex-col gap-1.5">
            <span className="label text-[11px] tracking-[0.12em]">{kind === 'move' ? 'New time' : 'Time'}</span>
            <div role="radiogroup" aria-label="Time" className="flex flex-wrap gap-1.5">
              {SLOTS.map(([from, to]) => (
                <button key={from} type="button" role="radio" aria-checked={!custom && start === from} onClick={() => pickSlot(from, to)} className={chip(!custom && start === from)}>
                  {fmtTime(from)}
                </button>
              ))}
              <button
                type="button"
                role="radio"
                aria-checked={custom}
                onClick={() => {
                  setCustom(true)
                  if (!start) changeStart(s?.meeting.start ?? SLOTS[0][0])
                }}
                className={chip(custom)}
              >
                Other time
              </button>
            </div>
            {!custom && start && (
              <p className="font-mono text-[12px] font-bold uppercase">
                {fmtRange(start, end)}
              </p>
            )}
          </div>
          {custom && (
            <>
              <div className="grid grid-cols-2 gap-2">
                <TimeButton label="Starts" value={start} open={openClock === 'start'} onOpen={() => setOpenClock(openClock === 'start' ? null : 'start')} />
                <TimeButton label="Ends" value={end} open={openClock === 'end'} invalid={end <= start} onOpen={() => setOpenClock(openClock === 'end' ? null : 'end')} />
              </div>
              {openClock && (
                <Clock
                  key={openClock}
                  label={openClock === 'start' ? 'Start time' : 'End time'}
                  value={openClock === 'start' ? start : end}
                  onChange={openClock === 'start' ? changeStart : setEnd}
                  chips={SLOTS.map(([a, b]) => (openClock === 'start' ? a : b))}
                  onDone={() => setOpenClock(null)}
                />
              )}
            </>
          )}
        </div>
      )}

      {picksRoom && (
        <div className="flex flex-col gap-1.5">
          <span className="label flex justify-between text-[11px] tracking-[0.12em]">
            Room
            {kind === 'room' && current && <span className="text-muted">Now in {current}</span>}
          </span>
          <div role="radiogroup" aria-label="Room" className="flex max-h-[148px] flex-wrap gap-1.5 overflow-y-auto">
            {kind !== 'room' && current && (
              <button type="button" role="radio" aria-checked={picked(current)} onClick={() => (setTyping(false), setRoom(current))} className={chip(picked(current))}>
                {current} (current)
              </button>
            )}
            <button type="button" role="radio" aria-checked={room === '' && !typing} onClick={() => (setTyping(false), setRoom(''))} className={chip(room === '' && !typing)}>
              To be announced
            </button>
            <button type="button" role="radio" aria-checked={typing} onClick={() => (setTyping(true), setRoom((r) => r ?? ''))} className={chip(typing)}>
              Type a room
            </button>
            {(free ?? [])
              .filter((r) => !sameRoom(r, current))
              .map((r) => (
                <button key={r} type="button" role="radio" aria-checked={picked(r)} onClick={() => (setTyping(false), setRoom(r))} className={chip(picked(r))}>
                  {r}
                </button>
              ))}
          </div>
          <p className="text-[13px] font-semibold text-muted">
            {!at ? 'Pick a time to see which rooms are free then.' : free === null ? 'Finding free rooms…' : free.length === 0 ? 'No room is free of classes then.' : 'These rooms are free of classes then.'}
          </p>
          {typing && <TextField label="Room" hideLabel placeholder="B202" value={room ?? ''} maxLength={60} autoFocus onChange={(e) => setRoom(e.target.value)} />}
        </div>
      )}

      {kind === 'extra' && (
        <div className="flex flex-col gap-1.5">
          <span className="label text-[11px] tracking-[0.12em]">Type</span>
          <div role="radiogroup" aria-label="Type" className="grid grid-cols-3 gap-1 rounded-full bg-navy/[0.07] p-1">
            {TYPES.map(([value, name]) => (
              <button key={value} type="button" role="radio" aria-checked={type === value} onClick={() => setType(value)} className={cx('h-11 rounded-full text-[15px] font-extrabold', type === value ? 'bg-navy text-cream' : 'text-navy')}>
                {name}
              </button>
            ))}
          </div>
        </div>
      )}

      <label className="flex flex-col gap-1.5">
        <span className="label flex justify-between text-[11px] tracking-[0.12em]">
          Note (optional)
          <span className="text-muted">
            {note.length}/{NOTE_MAX}
          </span>
        </span>
        <textarea
          value={note}
          maxLength={NOTE_MAX}
          rows={2}
          onChange={(e) => setNote(e.target.value)}
          className="field rounded-[14px] bg-paper p-3 text-[16px] font-semibold text-navy outline-none"
        />
        <span className="text-[13px] font-semibold text-muted">Students see this on the class and in the notification.</span>
      </label>

      {(preview?.warnings.length || preview?.problem || blocked) && (
        <div className="flex flex-col gap-1.5">
          {blocked && <Notice tone="warn">{blocked}</Notice>}
          {preview?.problem && !blocked && <Notice tone="warn">{preview.problem}</Notice>}
          {preview?.warnings.map((w) => (
            <Notice key={w.code} tone="warn">
              {w.text}
            </Notice>
          ))}
        </div>
      )}

      <div className="flex flex-col gap-2.5 pt-1" aria-live="polite">
        <ErrorText>{error || previewError}</ErrorText>
        {preview && !previewError && (
          <p className="text-[14px] font-semibold text-muted">
            {preview.size === 0 ? 'No Classy users have picked this class yet.' : `${preview.size} Classy ${preview.size === 1 ? 'user has' : 'users have'} this class. Those with alerts on are told at once.`}
            {preview.needsCode && ' You’ll be asked for a new sign-in code first.'}
          </p>
        )}
        <Button type="submit" className={cx('h-[52px]', kind === 'cancel' && can && 'bg-alert! text-cream!')} busy={busy} aria-disabled={!can || undefined}>
          {busy ? 'Saving…' : action}
        </Button>
      </div>
      {fresh.step}
    </form>
  )
}
