import { useEffect, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { ErrorText, Loading, Tag, TextField } from '../components/ui'
import { Filters, PageTitle, Th, seen } from './AdminLayout'

interface Row {
  id: number
  universityId: string
  isOwner: boolean
  major: string | null
  year: number | null
  step: string
  done: boolean
  needsRepick: boolean
  courses: number
  lastSeenAt: string | null
}

export const stepLabel = (step: string, done: boolean) =>
  done ? 'Done' : step === 'waiting' ? 'Waiting for semester' : `At ${step === 'ls' ? 'Life Skills' : step}`

type Filter = '' | 'pending' | 'done'

export function SetupTag({ step, done }: { step: string; done: boolean }) {
  if (done) return <Tag tone="plain">Done</Tag>
  if (step === 'waiting') return <Tag>Waiting</Tag>
  return (
    <span title={stepLabel(step, done)}>
      <Tag tone="alert">Stuck</Tag>
    </span>
  )
}

export default function Students() {
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const [q, setQ] = useState('')
  const status = (params.get('status') ?? '') as Filter
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
      <PageTitle title="Students" action={<span className="label text-[12px]">Only owners see this</span>} />
      <div className="flex flex-wrap items-end gap-3">
        <TextField label="Search by ID" placeholder="24-0161" inputMode="numeric" mono value={q} onChange={(e) => setQ(e.target.value)} className="flex-[1_1_280px]" />
        <Filters<Filter>
          value={status}
          onChange={(v) => setParams(v ? { status: v } : {}, { replace: true })}
          options={[
            ['', 'All'],
            ['pending', 'Stuck in setup'],
            ['done', 'Done'],
          ]}
        />
      </div>
      <ErrorText>{error}</ErrorText>
      {!data ? (
        <Loading />
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px] border-collapse text-[15px]">
            <thead>
              <tr className="border-b-[2.5px] border-navy">
                <Th>ID</Th>
                <Th>Major</Th>
                <Th right>Year</Th>
                <Th right>Courses</Th>
                <Th>Setup</Th>
                <Th>Last seen</Th>
              </tr>
            </thead>
            <tbody>
              {data.students.map((s) => (
                <tr key={s.id} onClick={() => navigate(`/admin/students/${s.id}`)} className="tint cursor-pointer border-b-[1.5px] border-navy/10">
                  <td className="px-2 py-3">
                    <span className="flex flex-wrap items-center gap-1.5">
                      <Link to={`/admin/students/${s.id}`} onClick={(e) => e.stopPropagation()} className="font-mono text-[14px] font-bold link-hover">
                        {s.universityId}
                      </Link>
                      {s.isOwner && <Tag tone="navy">Owner</Tag>}
                      {s.needsRepick && <Tag tone="alert">Re-pick</Tag>}
                    </span>
                  </td>
                  <td className="px-2 py-3 font-bold">{s.major ?? <span className="text-muted">No major</span>}</td>
                  <td className="px-2 py-3 text-right font-bold tabular-nums">{s.year ?? '-'}</td>
                  <td className="px-2 py-3 text-right font-bold tabular-nums">{s.courses || '-'}</td>
                  <td className="px-2 py-3">
                    <SetupTag step={s.step} done={s.done} />
                  </td>
                  <td className="px-2 py-3 font-semibold text-muted">{seen(s.lastSeenAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.students.length === 0 && <p className="mt-5 text-[16px] font-semibold">{q ? 'No student with that ID has signed in.' : 'No students here yet.'}</p>}
          {data.total > data.students.length && <p className="mt-3 text-[14px] font-semibold text-muted">Showing the first {data.students.length} of {data.total}. Search to narrow down.</p>}
        </div>
      )}
    </>
  )
}
