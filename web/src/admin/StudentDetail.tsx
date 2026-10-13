import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Section, Semester } from '../lib/types'
import { sectionSummary } from '../lib/time'
import { Badge, Button, Card, Confirm, ErrorText, Loading, Notice } from '../components/ui'
import { PageTitle } from './AdminLayout'
import { stepLabel } from './Students'

interface Detail {
  id: number
  universityId: string
  email: string
  isOwner: boolean
  year: number | null
  createdAt: string
  lastSeenAt: string | null
  major: { code: string; name: string } | null
  step: string
  done: boolean
  semester: Semester | null
  picks: { courseId: number; code: string; name: string; type: string; section: Section | null; sectionRemoved: boolean }[]
  clashes: [number, number][]
}

type Action = 'major' | 'picks' | 'remove' | 'owner' | null

export default function StudentDetail() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [d, setD] = useState<Detail | null>(null)
  const [error, setError] = useState('')
  const [action, setAction] = useState<Action>(null)
  const load = () => api.get<Detail>(`/admin/students/${id}`).then(setD, (e) => setError(errorText(e)))
  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!d) return <Loading />
  const clashing = new Set(d.clashes.flat())

  const actions: Record<Exclude<Action, null>, { title: string; body: string; label: string; danger?: boolean; run: () => Promise<unknown> }> = {
    major: { title: 'Reset major?', body: `${d.universityId} will pick their major, year and courses again at their next visit.`, label: 'Reset major', run: () => api.post(`/admin/students/${d.id}/reset`, { what: 'major' }) },
    picks: { title: 'Reset picks?', body: `${d.universityId} keeps their major and year but picks their courses again at their next visit.`, label: 'Reset picks', run: () => api.post(`/admin/students/${d.id}/reset`, { what: 'picks' }) },
    remove: { title: 'Remove student?', body: `This deletes ${d.universityId} and their picks. They can sign up again with their ID.`, label: 'Remove', danger: true, run: () => api.del(`/admin/students/${d.id}`) },
    owner: d.isOwner
      ? { title: 'Remove owner access?', body: `${d.universityId} will no longer see the dashboard.`, label: 'Remove access', danger: true, run: () => api.put(`/admin/students/${d.id}/owner`, { isOwner: false }) }
      : { title: 'Make owner?', body: `${d.universityId} will see the full dashboard, including every student. Only do this for partners running the app.`, label: 'Make owner', run: () => api.put(`/admin/students/${d.id}/owner`, { isOwner: true }) },
  }
  const a = action ? actions[action] : null

  return (
    <>
      <Link to="/admin/students" className="mb-3 inline-block text-sm text-zinc-500 hover:text-zinc-800 dark:hover:text-zinc-200">
        ← All students
      </Link>
      <PageTitle
        title={d.universityId}
        sub={
          <span className="flex flex-wrap items-center gap-2">
            <span>{d.major ? `${d.major.name} · Year ${d.year ?? '?'}` : 'No major yet'}</span>
            <Badge tone={d.done ? 'green' : 'gray'}>{stepLabel(d.step, d.done)}</Badge>
            {d.isOwner && <Badge tone="indigo">Owner</Badge>}
          </span>
        }
      />
      <div className="grid gap-5 lg:grid-cols-[1fr_18rem]">
        <section>
          <h2 className="mb-2 text-sm font-semibold text-zinc-500">Picks {d.semester && `· ${d.semester.name}`}</h2>
          {d.picks.length === 0 ? (
            <Notice>No courses picked{d.semester ? '' : ' (no semester published)'}.</Notice>
          ) : (
            <Card className="divide-y divide-zinc-100 dark:divide-zinc-800">
              {d.picks.map((p) => (
                <div key={p.courseId} className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{p.code}</span>
                    <span className="truncate text-zinc-500">{p.name}</span>
                    <span className="ml-auto text-xs text-zinc-400">{p.type}</span>
                  </div>
                  <div className="text-sm text-zinc-500">
                    {p.section ? (
                      <>
                        Section {p.section.number} · {sectionSummary(p.section)}
                        {clashing.has(p.section.id) && <span className="ml-1 text-amber-600">⚠ clash</span>}
                      </>
                    ) : (
                      <span className="text-amber-700 dark:text-amber-400">{p.sectionRemoved ? 'Section was removed; needs to re-pick' : 'No section'}</span>
                    )}
                  </div>
                </div>
              ))}
            </Card>
          )}
        </section>
        <aside className="space-y-3">
          <Card className="space-y-1 p-4 text-sm">
            <div className="text-zinc-500">Email</div>
            <div className="break-all">{d.email}</div>
            <div className="pt-2 text-zinc-500">Joined</div>
            <div>{new Date(d.createdAt).toLocaleDateString()}</div>
            {d.lastSeenAt && (
              <>
                <div className="pt-2 text-zinc-500">Last sign-in</div>
                <div>{new Date(d.lastSeenAt).toLocaleString()}</div>
              </>
            )}
          </Card>
          <div className="grid gap-2">
            <Button variant="secondary" onClick={() => setAction('major')}>Reset major</Button>
            <Button variant="secondary" onClick={() => setAction('picks')}>Reset picks</Button>
            <Button variant="secondary" onClick={() => setAction('owner')}>{d.isOwner ? 'Remove owner access' : 'Make owner'}</Button>
            <Button variant="ghost" className="text-red-600 dark:text-red-400" onClick={() => setAction('remove')}>Remove student</Button>
          </div>
        </aside>
      </div>
      <Confirm
        open={!!a}
        onClose={() => setAction(null)}
        title={a?.title ?? ''}
        body={a?.body}
        confirmLabel={a?.label ?? ''}
        danger={a?.danger}
        onConfirm={async () => {
          await a!.run()
          if (action === 'remove') navigate('/admin/students', { replace: true })
          else await load()
        }}
      />
    </>
  )
}
