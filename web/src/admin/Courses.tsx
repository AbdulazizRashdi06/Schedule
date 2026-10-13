import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorText, store } from '../lib/api'
import { ErrorText, Loading, Notice, Tag, TextField } from '../components/ui'
import { Filters, PageTitle, Th, useAdmin } from './AdminLayout'

interface CourseRow {
  id: number
  code: string
  name: string
  type: string
  year: number | null
  credits: number | null
  majors: string[]
  sections: number
  students: number
  noRoom: number
  removedPicks: number
  changed: boolean
}

type Filter = 'all' | 'fix' | 'changed'
/** A course needs a fix when a section has no room or students lost their section. */
const needsFix = (c: CourseRow) => c.noRoom > 0 || c.removedPicks > 0

export default function Courses() {
  const { semester } = useAdmin()
  const navigate = useNavigate()
  const [q, setQ] = useState(() => store.get('admin-course-q') ?? '')
  const [filter, setFilter] = useState<Filter>('all')
  const [list, setList] = useState<CourseRow[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!semester) return
    store.set('admin-course-q', q)
    const t = setTimeout(() => {
      api.get<CourseRow[]>(`/admin/semesters/${semester.id}/courses?q=${encodeURIComponent(q)}`).then(setList, (e) => setError(errorText(e)))
    }, 200)
    return () => clearTimeout(t)
  }, [semester, q])

  if (!semester) return <Notice>Create a semester under Upload first.</Notice>

  const rows = (list ?? []).filter((c) => (filter === 'fix' ? needsFix(c) : filter === 'changed' ? c.changed : true))

  return (
    <>
      <PageTitle title="Courses" action={<span className="label text-[12px]">{semester.name} · {list ? rows.length : '…'} shown</span>} />
      <div className="flex flex-wrap items-end gap-3">
        <TextField label="Search" placeholder="Code, title or instructor" value={q} onChange={(e) => setQ(e.target.value)} className="flex-[1_1_280px]" />
        <Filters<Filter> value={filter} onChange={setFilter} options={[['all', 'All'], ['fix', 'Needs fix'], ['changed', 'Changed']]} />
      </div>
      <ErrorText>{error}</ErrorText>
      {!list ? (
        <Loading />
      ) : list.length === 0 && !q ? (
        <Notice>No courses yet. Upload the schedule first.</Notice>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px] border-collapse text-[15px]">
            <thead>
              <tr className="border-b-[2.5px] border-navy">
                <Th>Code</Th>
                <Th>Title</Th>
                <Th>Majors</Th>
                <Th right>Sections</Th>
                <Th right>Students</Th>
                <Th>Status</Th>
              </tr>
            </thead>
            <tbody>
              {rows.map((c) => (
                <tr key={c.id} onClick={() => navigate(`/admin/courses/${c.id}`)} className="tint cursor-pointer border-b-[1.5px] border-navy/10">
                  <td className="px-2 py-3 font-mono text-[13px] font-bold">
                    <Link to={`/admin/courses/${c.id}`} onClick={(e) => e.stopPropagation()} className="link-hover">
                      {c.code}
                    </Link>
                  </td>
                  <td className="px-2 py-3 font-extrabold">{c.name}</td>
                  <td className="px-2 py-3 font-semibold text-muted">{c.majors.length ? c.majors.join(', ') : c.type === 'Core' ? '' : 'All'}</td>
                  <td className="px-2 py-3 text-right font-bold tabular-nums">{c.sections}</td>
                  <td className="px-2 py-3 text-right font-bold tabular-nums">{c.students}</td>
                  <td className="px-2 py-3">
                    {needsFix(c) ? <Tag tone="alert">Needs fix</Tag> : c.changed ? <Tag>Changed</Tag> : <Tag tone="plain">OK</Tag>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {rows.length === 0 && <p className="mt-5 text-[16px] font-semibold">{q ? `No course matches “${q}”.` : 'No courses in this filter.'}</p>}
        </div>
      )}
    </>
  )
}
