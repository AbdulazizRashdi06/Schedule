import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Badge, Button, Card, Checkbox, Confirm, ErrorText, Input, Loading, Notice, Sheet } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface MajorRow {
  id: number
  code: string
  name: string
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
  const [error, setError] = useState('')
  const [edit, setEdit] = useState<MajorRow | 'new' | null>(null)
  const [electives, setElectives] = useState<MajorRow | null>(null)
  const [removing, setRemoving] = useState<MajorRow | null>(null)
  const load = () => api.get<MajorRow[]>('/admin/majors').then(setList, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
  }, [])

  return (
    <>
      <PageTitle
        title="Majors"
        sub="Core courses come from the upload's Major and Type columns. Here you can name majors and allow extra electives."
        action={<Button onClick={() => setEdit('new')}>+ Add major</Button>}
      />
      <ErrorText>{error}</ErrorText>
      {!list ? (
        <Loading />
      ) : list.length === 0 ? (
        <Notice>No majors yet. They're created from the upload, or add one here.</Notice>
      ) : (
        <div className="grid gap-3 md:grid-cols-2">
          {list.map((m) => (
            <Card key={m.id} className="p-4">
              <div className="flex items-start gap-3">
                <span className="flex h-10 min-w-10 items-center justify-center rounded-xl bg-zinc-100 px-2 text-sm font-semibold dark:bg-zinc-800">{m.code}</span>
                <div className="min-w-0 flex-1">
                  <div className="font-medium">{m.name}</div>
                  <div className="text-sm text-zinc-500">
                    {m.coreCourses} core courses live · {m.students} students{m.extraElectives > 0 && ` · ${m.extraElectives} extra electives`}
                  </div>
                </div>
              </div>
              <div className="mt-3 flex flex-wrap gap-2">
                <Button variant="secondary" className="h-9 text-sm" onClick={() => setElectives(m)}>
                  Allowed electives
                </Button>
                <Button variant="ghost" className="h-9 text-sm" onClick={() => setEdit(m)}>
                  Rename
                </Button>
                <Button variant="ghost" className="h-9 text-sm text-red-600 dark:text-red-400" onClick={() => setRemoving(m)}>
                  Remove
                </Button>
              </div>
            </Card>
          ))}
        </div>
      )}

      <MajorForm
        major={edit}
        onClose={() => setEdit(null)}
        onSaved={() => {
          setEdit(null)
          load()
        }}
      />
      <ElectivesSheet major={electives} semesterId={semester?.id} onClose={() => { setElectives(null); load() }} />
      <Confirm
        open={!!removing}
        onClose={() => setRemoving(null)}
        title={`Remove ${removing?.name}?`}
        body="Only majors no course or student uses can be removed."
        confirmLabel="Remove"
        danger
        onConfirm={async () => {
          await api.del(`/admin/majors/${removing!.id}`)
          load()
        }}
      />
    </>
  )
}

function MajorForm({ major, onClose, onSaved }: { major: MajorRow | 'new' | null; onClose: () => void; onSaved: () => void }) {
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    setCode(major && major !== 'new' ? major.code : '')
    setName(major && major !== 'new' ? major.name : '')
    setError('')
  }, [major])
  const isNew = major === 'new'
  return (
    <Sheet open={!!major} onClose={onClose} title={isNew ? 'Add a major' : 'Rename major'}>
      <form
        className="space-y-3"
        onSubmit={async (e) => {
          e.preventDefault()
          setBusy(true)
          setError('')
          try {
            if (isNew) await api.post('/admin/majors', { code, name })
            else await api.put(`/admin/majors/${(major as MajorRow).id}`, { code, name })
            onSaved()
          } catch (err) {
            setError(errorText(err))
          } finally {
            setBusy(false)
          }
        }}
      >
        <Input placeholder="Code used in uploads, e.g. CS" value={code} onChange={(e) => setCode(e.target.value)} />
        <Input placeholder="Name, e.g. Computer Science" value={name} onChange={(e) => setName(e.target.value)} />
        {!isNew && <p className="text-xs text-zinc-500">If you change the code, use the new code in future uploads.</p>}
        <ErrorText>{error}</ErrorText>
        <Button className="w-full" busy={busy} disabled={!code.trim() || !name.trim()}>
          Save
        </Button>
      </form>
    </Sheet>
  )
}

function ElectivesSheet({ major, semesterId, onClose }: { major: MajorRow | null; semesterId?: number; onClose: () => void }) {
  const [data, setData] = useState<Electives | null>(null)
  const [allowed, setAllowed] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    setData(null)
    setError('')
    if (!major) return
    api.get<Electives>(`/admin/majors/${major.id}/electives${semesterId ? `?semesterId=${semesterId}` : ''}`).then((d) => {
      setData(d)
      setAllowed(new Set([...d.electives.filter((e) => e.source === 'allowed').map((e) => e.code), ...d.otherAllowed]))
    }, (e) => setError(errorText(e)))
  }, [major, semesterId])

  return (
    <Sheet open={!!major} onClose={onClose} title={`Electives for ${major?.name ?? ''}`}>
      <ErrorText>{error}</ErrorText>
      {!data ? (
        !error && <Loading />
      ) : data.electives.length === 0 ? (
        <Notice>No electives in this semester's schedule.</Notice>
      ) : (
        <div className="space-y-3">
          <p className="text-sm text-zinc-500">Tick the extra electives {data.major.code} students may take. Choices are kept by course code, so they carry over to later semesters.</p>
          <div className="-mx-1 max-h-[50vh] space-y-1 overflow-y-auto">
            {data.electives.map((e) => {
              const fixed = e.source === 'upload' || e.source === 'open'
              return (
                <div key={e.code} className="flex items-center gap-3 rounded-xl px-1 py-2">
                  <Checkbox
                    checked={fixed || allowed.has(e.code)}
                    label={e.code}
                    onChange={(v) => {
                      if (fixed) return
                      const next = new Set(allowed)
                      if (v) next.add(e.code)
                      else next.delete(e.code)
                      setAllowed(next)
                    }}
                  />
                  <div className="min-w-0 flex-1">
                    <span className="font-medium">{e.code}</span> <span className="text-zinc-500">{e.name}</span>
                  </div>
                  {e.source === 'upload' && <Badge>from upload</Badge>}
                  {e.source === 'open' && <Badge tone="green">open to all</Badge>}
                </div>
              )
            })}
          </div>
          <ErrorText>{error}</ErrorText>
          <Button
            className="w-full"
            busy={busy}
            onClick={async () => {
              setBusy(true)
              try {
                await api.put(`/admin/majors/${data.major.id}/electives`, { codes: [...allowed] })
                onClose()
              } catch (e) {
                setError(errorText(e))
              } finally {
                setBusy(false)
              }
            }}
          >
            Save
          </Button>
        </div>
      )}
    </Sheet>
  )
}
