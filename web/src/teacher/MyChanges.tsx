import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { fmtDay } from '../lib/slots'
import { fmtRange, fmtTime } from '../lib/time'
import type { ClassChange } from '../lib/types'
import { useHome } from '../lib/viewer'
import { ErrorText, Loading, Toast, cx, useToast } from '../components/ui'
import { BackButton } from '../student/timetable'
import { undoChanges } from './ChangeSheet'
import { useFreshCode } from './FreshCode'
import { clearTeachCache } from './teachData'

const KIND = { cancel: 'Cancelled', move: 'Moved', room: 'Room changed', extra: 'Extra class' } as const
const room = (r: string) => r || 'room to be announced'

/** A change in a line: what the class was, and what it is now. */
function describe(c: ClassChange) {
  const from = c.from ? `${fmtDay(c.from.date)} ${fmtTime(c.from.start)}` : ''
  if (c.kind === 'cancel') return from
  if (c.kind === 'move') return `${from} → ${c.to ? `${fmtDay(c.to.date)} ${fmtTime(c.to.start)}, ${room(c.to.room)}` : ''}`
  if (c.kind === 'room') return `${from} · ${c.from?.room || 'no room'} → ${room(c.to?.room ?? '')}`
  return c.to ? `${fmtDay(c.to.date)} ${fmtRange(c.to.start, c.to.end)}, ${room(c.to.room)}` : ''
}

/** Where the class is now (or was, for a cancelled one): the session page it opens. */
const sessionOf = (c: ClassChange) => {
  const at = c.to ?? c.from
  return at ? `/teach?session=s${c.sectionId}.${at.date}.${at.start}` : '/teach'
}

/**
 * My changes: the live changes this teacher made, or that someone made to their classes, the coming ones first, each
 * with who made it and Undo while it can still be undone.
 */
export default function MyChanges() {
  const navigate = useNavigate()
  const { teach } = useHome()
  const [changes, setChanges] = useState<ClassChange[] | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const fresh = useFreshCode()
  const toast = useToast()

  const load = useCallback(
    () =>
      api.get<{ changes: ClassChange[] }>('/teach/changes').then(
        (r) => (setChanges(r.changes), setError('')),
        (e) => setError(errorText(e)),
      ),
    [],
  )
  useEffect(() => {
    void load()
  }, [load])

  // One real class under several codes was changed in one go: one row.
  const groups = [...new Map((changes ?? []).map((c) => [c.group, (changes ?? []).filter((x) => x.group === c.group)])).values()]
  const live = groups.filter((g) => g[0].canUndo || g[0].canEdit)
  const past = groups.filter((g) => !g[0].canUndo && !g[0].canEdit).reverse()

  const undo = async (group: ClassChange[]) => {
    if (busy) return
    setBusy(group[0].group)
    setError('')
    try {
      await fresh.run(() => undoChanges(group))
      // The home draws it the next time it's opened.
      clearTeachCache()
      toast.show(group[0].kind === 'extra' ? 'Extra class removed.' : 'Undone. The class is as the timetable has it.')
      await load()
    } catch (e) {
      if (!fresh.cancelled(e)) setError(errorText(e))
    } finally {
      setBusy('')
    }
  }

  const list = (title: string, items: ClassChange[][]) =>
    items.length > 0 && (
      <section className="flex flex-col pt-6">
        <h2 className="display mb-2 text-[22px] tracking-[-0.02em]">{title}</h2>
        <ul>
          {items.map((group) => {
            const c = group[0]
            return (
              <li key={c.group} className="flex items-start gap-3 border-t-[2.5px] border-navy py-3.5">
                <Link to={sessionOf(c)} className="flex min-w-0 flex-1 flex-col gap-1">
                  <span className="flex flex-wrap items-center gap-1.5">
                    <span className="text-[18px] leading-[1.1] font-extrabold">{[...new Set(group.map((x) => x.code))].join(' · ')}</span>
                    <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', c.kind === 'cancel' ? 'bg-navy text-cream' : 'bg-ice text-navy shadow-[inset_0_0_0_1.5px_var(--color-navy)]')}>{KIND[c.kind]}</span>
                  </span>
                  <span className="font-mono text-[12px] font-bold uppercase">{describe(c)}</span>
                  {c.note && <span className="text-[14px] font-semibold text-muted">{c.note}</span>}
                  <span className="text-[13px] font-semibold text-muted">{c.mine ? 'By you' : `By ${c.by}`}</span>
                </Link>
                {c.canUndo && (
                  <button type="button" disabled={!!busy} onClick={() => void undo(group)} className="press label flex h-11 shrink-0 items-center rounded-full px-4 text-[12px] shadow-[inset_0_0_0_2px_color-mix(in_srgb,var(--color-navy)_25%,transparent)]">
                    {busy === c.group ? 'Undoing…' : c.kind === 'extra' ? 'Remove' : 'Undo'}
                  </button>
                )}
              </li>
            )
          })}
        </ul>
      </section>
    )

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(2.5rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between">
          <BackButton label="Back to today" onClick={() => navigate('/teach')} />
          {teach?.semester && <span className="label text-[12px] tracking-[0.14em]">{teach.semester.name}</span>}
        </header>
        <div className="flex flex-col gap-2 pt-5">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.05em]">My changes</h1>
          <p className="text-[16px] leading-snug font-semibold text-muted">Classes you cancelled, moved, re-roomed or added, and changes others made to your classes. To make one, open a class on your timetable.</p>
        </div>
        <ErrorText className="mt-4">{error}</ErrorText>
        {!changes ? (
          !error && <Loading rows={3} label="Loading your changes" />
        ) : groups.length === 0 ? (
          <p className="pt-6 text-[17px] font-semibold">No changes. Your classes are as the university timetable has them.</p>
        ) : (
          <>
            {list('Coming up', live)}
            {list('Earlier', past)}
          </>
        )}
      </div>
      <Toast toast={toast} className="inset-x-6 bottom-6 mx-auto max-w-[432px] rounded-[20px] bg-ice px-[18px] py-4 text-[16px] font-extrabold shadow-[0_12px_24px_-14px_rgb(11_25_87/0.5)]" />
      {fresh.step}
    </div>
  )
}
