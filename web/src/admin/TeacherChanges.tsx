import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { fmtDay } from '../lib/slots'
import { Button, ErrorText, Loading, Tag, Toast, cx, useToast } from '../components/ui'
import { Filters, PageTitle, useAdmin } from './AdminLayout'

interface At {
  date: string
  start: string
  end: string | null
  room: string
}
/** One teacher action on one real class (its codes share a group), in one state. */
interface Row {
  id: number
  group: string
  kind: 'cancel' | 'move' | 'room' | 'extra'
  type: string
  codes: string[]
  section: string
  name: string
  from: At | null
  to: At | null
  note: string | null
  /** What students see as the maker: a teacher's shown name, or "Classy staff". */
  by: string
  /** Who it really was: a staff email, a dashboard username; for RSA never a student's ID. */
  byLabel: string
  /** Set when an owner made it through "view as": the name being viewed. */
  onBehalfOf: string | null
  teacherId: number | null
  namedTeacher: string | null
  flagged: boolean
  at: string
  /** "live", or why it ended. */
  state: string
  endedBy: string | null
  endedAt: string | null
  /** Classy users in the class. Only ever a count. */
  students: number
  canUndo: boolean
}

type Filter = '' | 'live' | 'ended' | 'flagged'

const KIND = { cancel: 'Cancelled', move: 'Moved', room: 'Room changed', extra: 'Extra class' } as const
const ENDED: Record<string, string> = {
  undone: 'Undone',
  'undone-by-staff': 'Undone by staff',
  'undone-on-remove': 'Undone when the teacher was removed',
  replaced: 'Replaced by a newer change',
  merged: 'Now in the university timetable',
  'dropped-moved': 'Dropped: the university moved the class',
  'dropped-removed': 'Dropped: the university removed the class',
  'dropped-day-off': 'Dropped: day off',
}
const room = (r: string) => r || 'room to be announced'
const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { timeZone: 'Asia/Muscat', weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

/** A change in a line: what the class was, and what it is now. */
function describe(c: Row) {
  const from = c.from ? `${fmtDay(c.from.date)} ${c.from.start}` : ''
  if (c.kind === 'cancel') return from
  if (c.kind === 'move') return `${from} → ${c.to ? `${fmtDay(c.to.date)} ${c.to.start}, ${room(c.to.room)}` : ''}`
  if (c.kind === 'room') return `${from} · ${c.from?.room || 'no room'} → ${room(c.to?.room ?? '')}`
  return c.to ? `${fmtDay(c.to.date)} ${c.to.start}${c.to.end ? `–${c.to.end}` : ''}, ${room(c.to.room)}` : ''
}

/** Who made it, as the log names them: "Raed Bolbol (raed.bolbol@…)", "dev-owner, viewing as Raed Bolbol". */
function maker(c: Row) {
  if (c.onBehalfOf) return `${c.byLabel}, viewing as ${c.onBehalfOf}`
  return c.byLabel && c.byLabel !== c.by ? `${c.by} (${c.byLabel})` : c.by
}

/**
 * The log of every class change a teacher made, newest first, for owners and RSA: who, what, how many Classy users it
 * reaches, whether it is still on, and Undo until the class starts. Changes made by someone who isn't the class's
 * named teacher are flagged.
 */
export default function TeacherChanges() {
  const { semester } = useAdmin()
  const [params, setParams] = useSearchParams()
  const teacher = Number(params.get('teacher')) || null
  const filter = (params.get('show') ?? '') as Filter
  const key = `${semester?.id ?? 0}-${teacher ?? 0}-${filter}`
  // Rows are kept with the question they answer, so another semester or filter never shows the last one's rows.
  const [loaded, setLoaded] = useState<{ key: string; rows: Row[] } | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const toast = useToast()
  const rows = loaded?.key === key ? loaded.rows : null

  const load = useCallback(async () => {
    const q = new URLSearchParams()
    if (semester) q.set('semesterId', String(semester.id))
    if (teacher) q.set('teacherId', String(teacher))
    if (filter === 'live') q.set('live', 'true')
    if (filter === 'ended') q.set('live', 'false')
    if (filter === 'flagged') q.set('flagged', 'true')
    try {
      const r = await api.get<{ changes: Row[] }>(`/admin/teacher-changes?${q}`)
      setLoaded({ key, rows: r.changes })
      setError('')
    } catch (e) {
      setError(errorText(e))
    }
  }, [semester, teacher, filter, key])
  useEffect(() => {
    void load()
  }, [load])

  const set = (next: { show?: Filter; teacher?: number | null }) => {
    const show = next.show ?? filter
    const who = next.teacher === undefined ? teacher : next.teacher
    setParams({ ...(who ? { teacher: String(who) } : {}), ...(show ? { show } : {}) }, { replace: true })
  }

  const undo = async (c: Row) => {
    if (busy) return
    setBusy(c.group)
    setError('')
    try {
      await api.post(`/admin/teacher-changes/${encodeURIComponent(c.group)}/undo`)
      toast.show(c.kind === 'extra' ? 'Extra class removed.' : 'Undone. Students and the teacher are told.')
    } catch (e) {
      setError(errorText(e))
    } finally {
      await load()
      setBusy('')
    }
  }

  // The teacher the log is narrowed to, named from their own rows (the server sends an id, not a name).
  const who = teacher && rows?.find((r) => r.teacherId === teacher && !r.onBehalfOf)?.by

  return (
    <>
      <PageTitle
        title="Teacher changes"
        kicker={semester?.name}
        sub="Every class a teacher cancelled, moved, re-roomed or added, newest first. Undo works until the class starts."
      />
      <div className="flex flex-wrap items-center gap-3">
        <Filters<Filter>
          value={filter}
          onChange={(v) => set({ show: v })}
          options={[
            ['', 'All'],
            ['live', 'Live'],
            ['ended', 'Ended'],
            ['flagged', 'Not the named teacher'],
          ]}
        />
        {teacher && (
          <span className="flex items-center gap-2 rounded-full bg-ice py-1.5 pr-1.5 pl-4 text-[15px] font-extrabold">
            {who ? `Only ${who}` : 'One teacher only'}
            <button type="button" aria-label="Show every teacher" onClick={() => set({ teacher: null })} className="press grid size-9 place-items-center rounded-full text-[18px] leading-none">
              ×
            </button>
          </span>
        )}
      </div>
      <ErrorText>{error}</ErrorText>
      {!rows ? (
        !error && <Loading />
      ) : rows.length === 0 ? (
        <p className="border-y-[1.5px] border-navy/[0.12] px-2 py-3.5 text-[16px] font-semibold text-muted">
          {filter || teacher ? 'No changes match.' : `No teacher has changed a class${semester ? ` in ${semester.name}` : ''} yet.`}
        </p>
      ) : (
        <ul className="flex flex-col border-t-[2.5px] border-navy">
          {rows.map((c) => {
            const live = c.state === 'live'
            return (
              <li key={c.id} className={cx('tint grid grid-cols-[minmax(0,1fr)_auto] items-start gap-x-4 gap-y-2 border-b-[1.5px] border-navy/10 px-2 py-4', !live && 'text-navy/70')}>
                <div className="flex min-w-0 flex-col gap-1">
                  <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
                    <span className="text-[17px] leading-tight font-extrabold">
                      {c.codes.join(' · ')} <span className="font-semibold">section {c.section}</span>
                    </span>
                    <Tag tone={c.kind === 'cancel' ? 'navy' : 'ice'}>{KIND[c.kind]}</Tag>
                    {c.flagged && <Tag tone="alert">Not the named teacher</Tag>}
                  </span>
                  <span className="font-mono text-[13px] font-bold uppercase">{describe(c)}</span>
                  {c.note && <span className="text-[15px] font-semibold [overflow-wrap:anywhere]">“{c.note}”</span>}
                  <span className="text-[14px] font-semibold text-muted [overflow-wrap:anywhere]">
                    {when(c.at)} · by {maker(c)}
                    {c.flagged && c.namedTeacher && ` · listed under ${c.namedTeacher}`} · {c.students} Classy {c.students === 1 ? 'user' : 'users'}
                    {c.teacherId !== null && c.teacherId !== teacher && !c.onBehalfOf && (
                      <>
                        {' · '}
                        <Link to={`/admin/teacher-changes?teacher=${c.teacherId}`} className="font-bold text-navy link-hover">
                          Only theirs
                        </Link>
                      </>
                    )}
                  </span>
                  {!live && (
                    <span className="text-[14px] font-bold">
                      {ENDED[c.state] ?? c.state}
                      {c.endedAt && ` · ${when(c.endedAt)}`}
                      {c.endedBy && ` · by ${c.endedBy}`}
                    </span>
                  )}
                </div>
                <div className="flex flex-col items-end gap-2">
                  {live ? <Tag tone="sky">Live</Tag> : <Tag tone="plain">Ended</Tag>}
                  {c.canUndo && (
                    <Button variant="secondary" size="sm" busy={busy === c.group} disabled={!!busy && busy !== c.group} aria-label={`Undo: ${KIND[c.kind]} ${c.codes.join(' ')} section ${c.section}`} onClick={() => void undo(c)}>
                      {busy === c.group ? 'Undoing…' : c.kind === 'extra' ? 'Remove' : 'Undo'}
                    </Button>
                  )}
                </div>
              </li>
            )
          })}
        </ul>
      )}
      {rows && rows.length >= 200 && <p className="text-[14px] font-semibold text-muted">Showing the newest 200. Filter to narrow down.</p>}
      <Toast toast={toast} className="bottom-7 left-1/2 max-w-[calc(100vw-2rem)] -translate-x-1/2 rounded-full bg-navy px-[22px] py-3.5 text-center text-[15px] font-extrabold text-cream shadow-[0_12px_24px_-12px_rgb(11_25_87/0.6)]" />
    </>
  )
}
