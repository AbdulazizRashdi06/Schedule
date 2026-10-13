import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText, store } from '../lib/api'
import { Badge, Card, ErrorText, Input, Loading, Notice } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

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
}

export default function Courses() {
  const { semester } = useAdmin()
  const [q, setQ] = useState(() => store.get('admin-course-q') ?? '')
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

  return (
    <>
      <PageTitle title="Courses & sections" sub={`${semester.name} · tap a course to edit its sections`} />
      <Input placeholder="Search by code or name" value={q} onChange={(e) => setQ(e.target.value)} className="mb-4 max-w-md" />
      <ErrorText>{error}</ErrorText>
      {!list ? (
        <Loading />
      ) : list.length === 0 ? (
        <Notice>{q ? 'No courses match.' : 'No courses yet. Upload the schedule first.'}</Notice>
      ) : (
        <Card className="divide-y divide-zinc-100 overflow-hidden dark:divide-zinc-800">
          {list.map((c) => (
            <Link key={c.id} to={`/admin/courses/${c.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-zinc-50 dark:hover:bg-zinc-800/50">
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold">{c.code}</span>
                  <span className="truncate text-zinc-600 dark:text-zinc-400">{c.name}</span>
                </div>
                <div className="mt-0.5 flex flex-wrap gap-1.5 text-xs text-zinc-500">
                  <Badge tone={c.type === 'Core' ? 'indigo' : c.type === 'LS' ? 'green' : 'amber'}>{c.type}</Badge>
                  {c.year && <span>Year {c.year}</span>}
                  {c.majors.length > 0 && <span>{c.majors.join(', ')}</span>}
                </div>
              </div>
              <div className="text-right text-sm text-zinc-500 tabular-nums">
                <div>{c.sections} sec</div>
                <div>{c.students} student{c.students === 1 ? '' : 's'}</div>
              </div>
            </Link>
          ))}
        </Card>
      )}
    </>
  )
}
