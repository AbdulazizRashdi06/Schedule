import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Semester } from '../lib/types'
import { Card, ErrorText, Loading, Notice } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface Stats {
  semester: Semester | null
  studentsRegistered: number
  studentsFinished: number
  courses: number
  sections: number
  needRepick: number
  topSections: { id: number; courseId: number; code: string; number: string; students: number }[]
}

export default function AdminHome() {
  const { semester, isOwner } = useAdmin()
  const [stats, setStats] = useState<Stats | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    api.get<Stats>(`/admin/stats${semester ? `?semesterId=${semester.id}` : ''}`).then(setStats, (e) => setError(errorText(e)))
  }, [semester])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!stats) return <Loading />

  const numbers = [
    { label: 'Students registered', value: stats.studentsRegistered },
    { label: 'Finished setup', value: stats.studentsFinished, sub: stats.semester ? `for ${stats.semester.name}` : 'nothing published' },
    { label: 'Courses', value: stats.courses, sub: semester?.name },
    { label: 'Sections', value: stats.sections, sub: semester?.name },
  ]
  const shortcuts = [
    { to: '/admin/upload', label: 'Upload schedule', text: 'Add or update a semester from a spreadsheet' },
    { to: '/admin/courses', label: 'Edit a section', text: 'Change a time, room or instructor' },
    ...(isOwner ? [{ to: '/admin/students', label: 'Manage students', text: 'Picks, resets and owners' }] : [{ to: '/admin/majors', label: 'Majors', text: 'Majors and allowed electives' }]),
  ]

  return (
    <>
      <PageTitle title="Overview" sub={stats.semester ? `Students see ${stats.semester.name}` : 'No semester is published yet'} />
      {!stats.semester && (
        <div className="mb-5">
          <Notice tone="warn">
            Students can sign in and pick their major, but they'll wait until you <Link className="underline" to="/admin/upload">upload and publish</Link> a semester.
          </Notice>
        </div>
      )}
      {stats.needRepick > 0 && (
        <div className="mb-5">
          <Notice tone="warn">{stats.needRepick} student{stats.needRepick === 1 ? '' : 's'} need to re-pick a section that was removed.</Notice>
        </div>
      )}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {numbers.map((n) => (
          <Card key={n.label} className="p-4">
            <div className="text-sm text-zinc-500">{n.label}</div>
            <div className="mt-1 text-3xl font-semibold tabular-nums">{n.value}</div>
            {n.sub && <div className="text-xs text-zinc-400">{n.sub}</div>}
          </Card>
        ))}
      </div>
      <div className="mt-6 grid gap-3 md:grid-cols-3">
        {shortcuts.map((s) => (
          <Link key={s.to} to={s.to} className="rounded-2xl bg-white p-4 ring-1 ring-zinc-200/80 transition hover:ring-indigo-400 dark:bg-zinc-900 dark:ring-zinc-800">
            <div className="font-medium">{s.label} →</div>
            <div className="text-sm text-zinc-500">{s.text}</div>
          </Link>
        ))}
      </div>
      {stats.topSections.length > 0 && (
        <section className="mt-8">
          <h2 className="mb-2 text-sm font-semibold text-zinc-500">Busiest sections</h2>
          <Card className="divide-y divide-zinc-100 dark:divide-zinc-800">
            {stats.topSections.map((s) => (
              <Link key={s.id} to={`/admin/courses/${s.courseId}`} className="flex items-center justify-between px-4 py-2.5 hover:bg-zinc-50 dark:hover:bg-zinc-800/50">
                <span>
                  <span className="font-medium">{s.code}</span>-{s.number}
                </span>
                <span className="text-sm text-zinc-500 tabular-nums">{s.students} student{s.students === 1 ? '' : 's'}</span>
              </Link>
            ))}
          </Card>
        </section>
      )}
    </>
  )
}
