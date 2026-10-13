import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Badge, Card, ErrorText, Input, Loading, Notice, Select } from '../components/ui'
import { PageTitle } from './AdminLayout'

interface Row {
  id: number
  universityId: string
  isOwner: boolean
  major: string | null
  year: number | null
  step: string
  done: boolean
  needsRepick: boolean
  lastSeenAt: string | null
}

export const stepLabel = (step: string, done: boolean) =>
  done ? 'Done' : step === 'waiting' ? 'Waiting for semester' : `At ${step === 'ls' ? 'Life Skills' : step}`

export default function Students() {
  const [q, setQ] = useState('')
  const [status, setStatus] = useState('')
  const [data, setData] = useState<{ total: number; students: Row[] } | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    const t = setTimeout(() => {
      api.get<{ total: number; students: Row[] }>(`/admin/students?q=${encodeURIComponent(q)}&status=${status}`).then(setData, (e) => setError(errorText(e)))
    }, 200)
    return () => clearTimeout(t)
  }, [q, status])

  return (
    <>
      <PageTitle title="Students" sub={data ? `${data.total} student${data.total === 1 ? '' : 's'}` : undefined} />
      <div className="mb-4 flex flex-wrap gap-2">
        <Input placeholder="Search ID or major" value={q} onChange={(e) => setQ(e.target.value)} className="max-w-xs" />
        <Select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Everyone</option>
          <option value="done">Finished setup</option>
          <option value="pending">Not finished</option>
        </Select>
      </div>
      <ErrorText>{error}</ErrorText>
      {!data ? (
        <Loading />
      ) : data.students.length === 0 ? (
        <Notice>No students found.</Notice>
      ) : (
        <Card className="divide-y divide-zinc-100 overflow-hidden dark:divide-zinc-800">
          {data.students.map((s) => (
            <Link key={s.id} to={`/admin/students/${s.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-zinc-50 dark:hover:bg-zinc-800/50">
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="font-mono font-medium">{s.universityId}</span>
                  {s.isOwner && <Badge tone="indigo">Owner</Badge>}
                  {s.needsRepick && <Badge tone="amber">Needs re-pick</Badge>}
                </div>
                <div className="text-sm text-zinc-500">
                  {s.major ?? 'No major'}
                  {s.year && ` · Year ${s.year}`}
                </div>
              </div>
              <Badge tone={s.done ? 'green' : 'gray'}>{stepLabel(s.step, s.done)}</Badge>
            </Link>
          ))}
        </Card>
      )}
      {data && data.total > data.students.length && <p className="mt-2 text-sm text-zinc-500">Showing the first {data.students.length}. Search to narrow down.</p>}
    </>
  )
}
