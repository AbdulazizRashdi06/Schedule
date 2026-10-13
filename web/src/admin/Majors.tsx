import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, Confirm, ErrorText, Icon, Loading, Notice, Tag, TextField, cx } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface MajorRow {
  id: number
  code: string
  name: string
  family: string | null
  coreCourses: number
  extraElectives: number
  students: number
}

interface Electives {
  major: { id: number; code: string; name: string }
  semesterId: number | null
  electives: { code: string; name: string; source: 'upload' | 'open' | 'allowed' | 'no' }[]
  otherAllowed: string[]
}

export default function Majors() {
  const { semester } = useAdmin()
  const [list, setList] = useState<MajorRow[] | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [error, setError] = useState('')
  const load = () =>
    api.get<MajorRow[]>('/admin/majors').then((l) => {
      setList(l)
      setSelected((id) => (l.some((m) => m.id === id) ? id : (l[0]?.id ?? null)))
    }, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
  }, [])

  const major = list?.find((m) => m.id === selected) ?? null

  return (
    <>
      <PageTitle title="Majors" />
      <ErrorText>{error}</ErrorText>
      {!list ? (
        <Loading />
      ) : (
        <div className="flex flex-wrap items-start gap-8">
          <section aria-label="All majors" className="flex flex-[1_1_300px] flex-col gap-1.5">
            {list.length === 0 && <p className="text-[15px] font-semibold text-muted">No majors yet. They’re created from the upload, or add one below.</p>}
            {list.map((m) => (
              <button
                key={m.id}
                type="button"
                aria-pressed={m.id === selected}
                onClick={() => setSelected(m.id)}
                className={cx('press flex min-h-[52px] items-center justify-between gap-3 rounded-[14px] px-3.5 py-2 text-left', m.id === selected ? 'bg-navy text-cream' : 'tint')}
              >
                <span className="flex min-w-0 flex-col">
                  <span className="text-[16px] font-extrabold">{m.name}</span>
                  {m.family && <span className="font-mono text-[11px] font-bold opacity-70">{m.family}</span>}
                </span>
                <span className="label shrink-0 text-[11px] tracking-normal">{m.students === 0 && m.coreCourses === 0 ? 'New' : `${m.students} ${m.students === 1 ? 'student' : 'students'}`}</span>
              </button>
            ))}
            <AddMajor
              onAdded={async (id) => {
                await load()
                setSelected(id)
              }}
            />
          </section>
          {major && <MajorPanel key={major.id} major={major} semesterId={semester?.id} onChanged={load} />}
        </div>
      )}
    </>
  )
}

function AddMajor({ onAdded }: { onAdded: (id: number) => void }) {
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <form
      className="flex flex-col gap-2 pt-2.5"
      onSubmit={async (e) => {
        e.preventDefault()
        if (!code.trim() || !name.trim()) return setError('Enter a code (as used in uploads) and a name.')
        setBusy(true)
        setError('')
        try {
          const m = await api.post<{ id: number }>('/admin/majors', { code, name })
          setCode('')
          setName('')
          onAdded(m.id)
        } catch (err) {
          setError(errorText(err))
        } finally {
          setBusy(false)
        }
      }}
    >
      <div className="flex gap-2">
        <TextField label="New major code" hideLabel placeholder="Code" value={code} onChange={(e) => setCode(e.target.value)} className="w-24" mono />
        <TextField label="New major name" hideLabel placeholder="New major" value={name} onChange={(e) => setName(e.target.value)} className="min-w-0 flex-1" />
        <Button type="submit" busy={busy} className="px-[18px] text-[15px]">
          Add
        </Button>
      </div>
      <ErrorText>{error}</ErrorText>
    </form>
  )
}

function MajorPanel({ major, semesterId, onChanged }: { major: MajorRow; semesterId?: number; onChanged: () => void }) {
  const [data, setData] = useState<Electives | null>(null)
  const [allowed, setAllowed] = useState<Set<string>>(new Set())
  const [renaming, setRenaming] = useState(false)
  const [code, setCode] = useState(major.code)
  const [name, setName] = useState(major.name)
  const [family, setFamily] = useState(major.family ?? '')
  const [removing, setRemoving] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    api.get<Electives>(`/admin/majors/${major.id}/electives${semesterId ? `?semesterId=${semesterId}` : ''}`).then(
      (d) => {
        setData(d)
        setAllowed(new Set([...d.electives.filter((e) => e.source === 'allowed').map((e) => e.code), ...d.otherAllowed]))
      },
      (e) => setError(errorText(e)),
    )
  }, [major.id, semesterId])

  // Ticks save straight away.
  const toggle = async (c: string, on: boolean) => {
    const next = new Set(allowed)
    if (on) next.add(c)
    else next.delete(c)
    setAllowed(next)
    setStatus('Saving…')
    setError('')
    try {
      await api.put(`/admin/majors/${major.id}/electives`, { codes: [...next] })
      setStatus('Saved.')
      onChanged()
    } catch (e) {
      setAllowed(allowed)
      setStatus('')
      setError(errorText(e))
    }
  }

  const open = data ? data.electives.filter((e) => e.source !== 'no' || allowed.has(e.code)).length : 0

  return (
    <section aria-label="Selected major" className="flex flex-[2_1_420px] flex-col gap-[18px] border-t-[2.5px] border-navy pt-[18px]">
      {renaming ? (
        <form
          className="flex flex-wrap gap-2"
          onSubmit={async (e) => {
            e.preventDefault()
            setError('')
            try {
              await api.put(`/admin/majors/${major.id}`, { code, name, family })
              setRenaming(false)
              onChanged()
            } catch (err) {
              setError(errorText(err))
            }
          }}
        >
          <TextField label="Code" hideLabel value={code} onChange={(e) => setCode(e.target.value)} mono className="w-24" boxClassName="h-[52px]" />
          <TextField label="Major name" hideLabel autoFocus value={name} onChange={(e) => setName(e.target.value)} className="min-w-0 flex-1" boxClassName="h-[52px] [&_input]:text-[20px] [&_input]:font-extrabold" />
          <TextField label="Family" hideLabel placeholder="Family" value={family} onChange={(e) => setFamily(e.target.value)} mono className="w-28" boxClassName="h-[52px]" />
          <Button type="submit" className="h-[52px] px-5 text-[15px]">
            Save
          </Button>
          <Button type="button" variant="ghost" className="h-[52px] px-4 text-[15px]" onClick={() => setRenaming(false)}>
            Cancel
          </Button>
        </form>
      ) : (
        <div className="flex flex-wrap items-end justify-between gap-3">
          <h2 className="text-[40px] leading-none font-extrabold tracking-[-0.03em]">{major.name}</h2>
          <div className="flex gap-1.5">
            <Button variant="secondary" size="sm" onClick={() => setRenaming(true)}>
              Edit
            </Button>
            <Button variant="danger" size="sm" onClick={() => setRemoving(true)}>
              Remove
            </Button>
          </div>
        </div>
      )}
      <span className="text-[15px] font-semibold text-muted">
        <span className="font-mono font-bold">{major.code}</span> in uploads ·{' '}
        {major.family ? (
          <>
            family <span className="font-mono font-bold">{major.family}</span> (shares its courses) ·
          </>
        ) : (
          'no family ·'
        )} {major.students} {major.students === 1 ? 'student' : 'students'} · {major.coreCourses} core{' '}
        {major.coreCourses === 1 ? 'course' : 'courses'} live
      </span>
      <ErrorText>{error}</ErrorText>
      <fieldset className="flex flex-col gap-1">
        <legend className="pb-2 text-[20px] font-extrabold">Allowed electives</legend>
        {!data ? (
          !error && <Loading rows={3} />
        ) : data.electives.length === 0 ? (
          <Notice>No electives in this semester’s schedule.</Notice>
        ) : (
          data.electives.map((e) => {
            const fixed = e.source === 'upload' || e.source === 'open'
            const on = fixed || allowed.has(e.code)
            return (
              <button
                key={e.code}
                type="button"
                role="checkbox"
                aria-checked={on}
                aria-disabled={fixed}
                onClick={() => !fixed && toggle(e.code, !on)}
                className="press tint flex min-h-[52px] items-center gap-3 rounded-[12px] px-2 py-1.5 text-left"
              >
                <span className={cx('grid size-[26px] shrink-0 place-items-center rounded-[8px]', on ? (fixed ? 'bg-navy/40 text-cream' : 'bg-navy text-cream') : 'border-2 border-navy/35')}>
                  {on && <Icon name="check" className="pop size-3.5" strokeWidth={3.6} />}
                </span>
                <span className="flex min-w-0 flex-1 flex-col gap-px">
                  <span className="text-[16px] font-extrabold">{e.name}</span>
                  <span className="font-mono text-[11px] font-bold text-muted">{e.code}</span>
                </span>
                {e.source === 'upload' && <Tag tone="plain">From upload</Tag>}
                {e.source === 'open' && <Tag tone="plain">Open to all</Tag>}
              </button>
            )
          })
        )}
      </fieldset>
      {data && data.electives.length > 0 && (
        <span className="text-[14px] font-semibold text-muted" aria-live="polite">
          {open} of {data.electives.length} electives open to {major.name}. Changes save right away.{status && ` ${status}`}
        </span>
      )}
      <Confirm
        open={removing}
        onClose={() => setRemoving(false)}
        title={`Remove ${major.name}?`}
        body="Only majors that no course or student uses can be removed."
        confirmLabel="Remove"
        danger
        onConfirm={async () => {
          await api.del(`/admin/majors/${major.id}`)
          onChanged()
        }}
      />
    </section>
  )
}
