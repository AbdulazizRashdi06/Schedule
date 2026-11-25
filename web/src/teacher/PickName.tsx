import { useEffect, useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useHome } from '../lib/viewer'
import { Confirm, ErrorText, Loading, TextField, cx } from '../components/ui'
import { BackButton } from '../student/timetable'
import { clearTeachCache } from './teachData'

interface NameRow {
  name: string
  sections: number
}

/**
 * The name step: a teacher whose email didn't match a name on the timetable picks theirs, and staff confirm it.
 * "My name isn't here" carries on with an empty timetable. Staff accounts only.
 */
export default function PickName() {
  const { teach } = useHome()
  const navigate = useNavigate()
  const [names, setNames] = useState<NameRow[] | null>(null)
  const [q, setQ] = useState('')
  const [picked, setPicked] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const staff = teach?.kind === 'staff'

  useEffect(() => {
    if (!staff) return
    api.get<{ names: NameRow[] }>('/teach/names').then(
      (r) => setNames(r.names),
      (e) => setError(errorText(e)),
    )
  }, [staff])

  // A teaching assistant's names are attached by staff, and an owner looking in doesn't pick for the teacher.
  if (teach && !staff) return <Navigate to="/teach" replace />

  const done = () => {
    // The home loads again: the waiting banner, or the empty timetable.
    clearTeachCache()
    navigate('/teach', { replace: true })
  }
  const send = (body: { name: string } | { none: true }) => api.post('/teach/names', body).then(done)

  const term = q.trim().toLowerCase()
  const list = (names ?? []).filter((n) => !term || n.name.toLowerCase().includes(term))
  const waiting = teach?.names.find((n) => n.state === 'pending')?.name
  // The first time through there is nowhere to go back to: the choice is a name, or "My name isn't here".
  const canLeave = teach != null && teach.status !== 'pick-name'

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        <header className="flex min-h-12 items-center justify-between gap-3">
          {canLeave ? <BackButton label="Back to today" onClick={() => navigate('/teach')} /> : <span />}
          <span className="label text-[12px] tracking-[0.14em]">{teach?.semester?.name ?? 'Classy'}</span>
        </header>
        <div className="flex flex-col gap-2 pt-5">
          <h1 className="display text-[52px] leading-[0.88] tracking-[-0.05em]">Which one is you?</h1>
          <p className="text-[16px] leading-snug font-semibold text-muted">
            Pick your name as the university timetable writes it. Classy staff confirm it, then your classes appear on their own.
            {waiting && ` You asked for ${waiting}; picking another replaces that.`}
          </p>
        </div>

        <TextField label="Search" hideLabel value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search for your name" boxClassName="h-[52px] rounded-2xl" className="mt-5 [&_input]:text-[17px]" />
        <ErrorText className="mt-3">{error}</ErrorText>

        {!names && !error ? (
          <Loading rows={6} label="Loading the names" />
        ) : (
          <ul aria-label="Names on the timetable" className="mt-3 border-t-[2.5px] border-navy">
            {list.map((n) => (
              <li key={n.name} className="border-b-[1.5px] border-navy/10">
                <button type="button" onClick={() => setPicked(n.name)} className="tint flex min-h-14 w-full items-center justify-between gap-3 py-2 text-left">
                  <span className="min-w-0 text-[18px] leading-tight font-extrabold">{n.name}</span>
                  <span className="label shrink-0 text-[11px] text-muted">
                    {n.sections} {n.sections === 1 ? 'class' : 'classes'}
                  </span>
                </button>
              </li>
            ))}
            {names && list.length === 0 && <li className="py-4 text-[15px] font-semibold text-muted">No name matches. Try fewer letters, or carry on without one.</li>}
          </ul>
        )}

        <div className="mt-auto flex flex-col gap-2 pt-8">
          <button
            type="button"
            aria-disabled={busy}
            onClick={() => {
              if (busy) return
              setBusy(true)
              setError('')
              send({ none: true }).catch((e) => {
                setError(errorText(e))
                setBusy(false)
              })
            }}
            className={cx('press label flex h-14 items-center justify-center rounded-full border-[2.5px] border-navy text-[13px]', busy && 'opacity-60')}
          >
            {busy ? 'One moment…' : 'My name isn’t here'}
          </button>
          <p className="text-center text-[13px] font-semibold text-muted">You can still add the classes you teach by hand.</p>
        </div>
      </div>

      <Confirm
        open={!!picked}
        onClose={() => setPicked(null)}
        title={`You are ${picked ?? ''}?`}
        body="Classy staff check this before your classes appear. Until then you can add the classes you teach yourself."
        confirmLabel="Yes, that’s me"
        onConfirm={() => send({ name: picked ?? '' })}
      />
    </div>
  )
}
