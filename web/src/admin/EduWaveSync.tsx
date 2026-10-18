import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import type { Semester } from '../lib/types'
import { Button, Confirm, ErrorText } from '../components/ui'
import { CheckStep, type Preview } from './Upload'

interface Pending {
  id: number
  receivedAt: string
  source: string
  courses: number
  classes: number
  failed: string
  preview: Preview
}

const when = (iso: string) => new Date(iso).toLocaleString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

/** The latest timetable the Classy Sync extension fetched from EduWave: check it and apply it like an upload. */
export default function EduWaveSync({ semester }: { semester: Semester }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const [lastApplied, setLastApplied] = useState<string | null>(null)
  const [open, setOpen] = useState(false)
  const [confirm, setConfirm] = useState(false)
  const [done, setDone] = useState('')
  const [error, setError] = useState('')

  const load = () =>
    api.get<{ pending: Pending | null; lastApplied: string | null }>(`/admin/semesters/${semester.id}/sync`).then(
      (r) => {
        setPending(r.pending)
        setLastApplied(r.lastApplied)
      },
      (e) => setError(errorText(e)),
    )
  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [semester.id])

  if (!pending && !lastApplied && !done && !error) return null
  const p = pending?.preview
  const changes = p ? p.added.length + p.updated.length + p.removed.length : 0

  return (
    <section className="flex max-w-[860px] flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-[20px] font-extrabold">From EduWave</h2>
        {!pending && lastApplied && <span className="text-[14px] font-semibold text-muted">Last sync applied {when(lastApplied)}. Nothing new.</span>}
      </div>
      {done && <p className="text-[15px] font-bold">{done}</p>}
      <ErrorText>{error}</ErrorText>
      {pending && p && (
        <>
          <p className="text-[15px] font-semibold">
            Fetched {when(pending.receivedAt)} by {pending.source}: {pending.courses} courses, {pending.classes.toLocaleString()} classes.{' '}
            {changes === 0 ? 'Nothing changed.' : `${p.added.length} added, ${p.updated.length} changed, ${p.removed.length} removed.`}
          </p>
          {pending.failed && <p className="text-[14px] font-semibold text-muted">EduWave didn’t answer for {pending.failed}. Their classes stay as they are.</p>}
          {!open ? (
            <div className="flex flex-wrap gap-2.5">
              <Button variant="secondary" onClick={() => setOpen(true)}>
                Check changes
              </Button>
              <Button disabled={!p.canApply || changes === 0} onClick={() => setConfirm(true)}>
                Apply
              </Button>
            </div>
          ) : (
            <CheckStep p={p} backLabel="Hide" onBack={() => setOpen(false)} onNext={() => setConfirm(true)} />
          )}
          <Confirm
            open={confirm}
            title={`Apply to ${semester.name}?`}
            body={`${p.added.length} added, ${p.updated.length} changed, ${p.removed.length} removed.${p.studentsAffected ? ` ${p.studentsAffected} students will see the Changed badge.` : ''}`}
            confirmLabel="Apply"
            onClose={() => setConfirm(false)}
            onConfirm={async () => {
              await api.post(`/admin/semesters/${semester.id}/sync/apply`, { id: pending.id })
              setDone('Applied. Students see the new timetable now.')
              setOpen(false)
              await load()
            }}
          />
        </>
      )}
    </section>
  )
}
