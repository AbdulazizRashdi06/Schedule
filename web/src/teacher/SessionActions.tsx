import { useState } from 'react'
import { errorText } from '../lib/api'
import { blockOn, isoDate, nowMinutes, type Session } from '../lib/slots'
import type { TeachMe, Timetable } from '../lib/types'
import { cx } from '../components/ui'
import ChangeSheet, { undoChanges, type ChangeDone, type ChangeRequest } from './ChangeSheet'
import { useFreshCode } from './FreshCode'
import { blockSize } from './teachSessions'

/**
 * A teacher's part of the session page, inside its navy panel: how many Classy users the class has, whose name it is
 * under, and what can be done to this one session: cancel, move, another room, an extra class, and undoing or
 * editing a change already made.
 */
export default function SessionActions({
  s,
  date,
  now,
  tt,
  teach,
  offline,
  afterChange,
}: {
  s: Session
  date: Date
  now: Date
  tt: Timetable | null
  teach: TeachMe | null
  offline: Date | null
  afterChange: (done: ChangeDone) => void
}) {
  const [request, setRequest] = useState<ChangeRequest | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const fresh = useFreshCode()
  // Own slots, office hours and the reps' events aren't classes to change.
  if (s.mine || s.office || s.plannerEventId) return null

  const members = s.off ? (s.merged ?? [s]) : blockOn(s, date)
  const on = members.length > 0 ? members : (s.merged ?? [s])
  const size = s.off ? on.reduce((n, m) => n + (m.cls.students ?? 0), 0) : blockSize(s, date, tt)
  // Listed under someone else: they are told about any change.
  const others = [...new Set(on.filter((m) => !m.cls.own).map((m) => m.cls.section.instructor.trim()))]

  const change = s.change
  const iso = isoDate(date)
  const started = iso < isoDate(now) || (iso === isoDate(now) && s.start <= nowMinutes(now))
  // An owner looking in passes the switch, as on the server.
  const switchedOff = !!teach && !teach.changesEnabled && teach.kind !== 'view'
  /** Why nothing can be done at all, if so. */
  const stopped = offline ? 'You’re offline. Changes need a connection.' : teach?.frozen ? 'Your class changes are paused. Ask the Classy team.' : ''
  /** Why nothing new can be made (undoing still works). */
  const noNew = stopped || (switchedOff ? 'Class changes are switched off for now.' : '')
  const canUndo = !stopped && !!change?.canUndo
  const canEdit = !noNew && !!change?.canEdit
  const canChange = !noNew && !started

  const edit = (kind: 'cancel' | 'move' | 'room') => () => setRequest({ kind, s, date })
  const extra = () => setRequest({ kind: 'extra', date: iso, ticked: on.map((m) => m.cls.section.id) })
  const undo = (text: string) => async () => {
    if (!change || busy) return
    setBusy(true)
    setError('')
    try {
      const all = (s.merged ?? [s]).map((m) => m.change!).filter(Boolean)
      await fresh.run(() => undoChanges(all))
      afterChange({ text, changes: [], undone: change })
    } catch (e) {
      if (!fresh.cancelled(e)) setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  type Act = { label: string; go: () => void; on: boolean; tone?: 'cream' | 'alert' }
  let acts: Act[]
  let reason = ''
  if (!change) {
    acts = [
      { label: 'Cancel class', go: edit('cancel'), on: canChange, tone: 'alert' },
      { label: 'Move', go: edit('move'), on: canChange },
      { label: 'Change room', go: edit('room'), on: canChange },
      { label: 'Add extra class', go: extra, on: !noNew },
    ]
    reason = noNew || (started ? 'This class has already started.' : '')
  } else if (s.off === 'cancelled') {
    acts = [
      { label: 'Undo, class is back on', go: undo('Undone. The class is back on.'), on: canUndo, tone: 'cream' },
      { label: 'Move instead', go: edit('move'), on: canEdit },
    ]
    reason = stopped || (!change.canUndo && !change.canEdit ? 'This class has already started.' : !canEdit && noNew ? noNew : '')
  } else if (change.kind === 'move') {
    acts = [
      { label: 'Undo move', go: undo('Undone. The class is back at its usual time.'), on: canUndo, tone: 'cream' },
      { label: 'Move again', go: edit('move'), on: canEdit },
      { label: 'Change room', go: edit('room'), on: canEdit },
      { label: 'Cancel class', go: edit('cancel'), on: canEdit, tone: 'alert' },
    ]
    reason = stopped || (!change.canUndo && !change.canEdit ? 'This class has already started.' : !canEdit && noNew ? noNew : !change.canUndo ? 'The original time has passed, so the move can’t be undone.' : '')
  } else if (change.kind === 'room') {
    acts = [
      { label: 'Undo', go: undo('Undone. The class is back in its usual room.'), on: canUndo, tone: 'cream' },
      { label: 'Change room', go: edit('room'), on: canEdit },
      { label: 'Cancel class', go: edit('cancel'), on: canEdit, tone: 'alert' },
      { label: 'Move', go: edit('move'), on: canEdit },
    ]
    reason = stopped || (!change.canUndo && !change.canEdit ? 'This class has already started.' : !canEdit && noNew ? noNew : '')
  } else {
    acts = [
      { label: 'Remove extra class', go: undo('Extra class removed.'), on: canUndo, tone: 'alert' },
      { label: 'Edit', go: () => setRequest({ kind: 'extra', edit: s }), on: canEdit },
    ]
    reason = stopped || (!change.canUndo && !change.canEdit ? 'This class has already started.' : !canEdit && noNew ? noNew : '')
  }

  return (
    <div className="flex flex-col gap-3 rounded-[18px] bg-navy-800 px-3.5 py-3.5">
      <div className="flex flex-col gap-0.5">
        <p className="text-[16px] leading-tight font-extrabold">{size === null ? 'Classy users' : `${size} Classy ${size === 1 ? 'user' : 'users'} in this class`}</p>
        {on.length > 1 && <p className="font-mono text-[12px] font-bold text-ice uppercase">{on.map((m) => `${m.cls.code} ${m.cls.students ?? 0}`).join(' · ')}</p>}
        <p className="text-[13px] font-semibold text-ice">Not the official enrollment.</p>
      </div>
      {others.length > 0 && (
        <p className="text-[13px] font-semibold text-ice">
          {others.filter(Boolean).length > 0 ? `Listed under ${others.filter(Boolean).join(' and ')}. They’ll be told about any change.` : 'Not listed under your name.'}
        </p>
      )}
      <div className="grid grid-cols-2 gap-2">
        {acts.map((a, i) => (
          <button
            key={a.label}
            type="button"
            disabled={!a.on || busy}
            onClick={a.go}
            className={cx(
              'press flex min-h-12 items-center justify-center rounded-full px-3 py-2 text-center text-[14px] leading-tight font-extrabold',
              // An odd one out fills the row.
              acts.length % 2 === 1 && i === 0 && 'col-span-2',
              !a.on
                ? 'text-cream/40 shadow-[inset_0_0_0_2px_rgb(255_253_248/0.14)]'
                : a.tone === 'cream'
                  ? 'bg-cream text-navy'
                  : a.tone === 'alert'
                    ? 'bg-alert-bg text-alert-ink'
                    : 'text-cream shadow-[inset_0_0_0_2px_rgb(255_253_248/0.35)]',
            )}
          >
            {a.label}
          </button>
        ))}
      </div>
      {(error || reason) && (
        <p role="status" className={cx('text-[13px] font-semibold', error ? 'text-alert-line' : 'text-ice')}>
          {error || reason}
        </p>
      )}
      <ChangeSheet request={request} onClose={() => setRequest(null)} onDone={afterChange} />
      {fresh.step}
    </div>
  )
}
