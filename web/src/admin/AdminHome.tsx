import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Semester } from '../lib/types'
import { ErrorText, Loading, Notice, Sk } from '../components/ui'
import { PageTitle, shortDate, useAdmin } from './AdminLayout'

interface Stats {
  semester: Semester | null
  studentsRegistered: number
  studentsFinished: number
  courses: number
  sections: number
  needRepick: number
  sectionEditsThisWeek: number
  attention: {
    removed: { courseId: number; code: string; students: number }[]
    noRoom: { courseId: number; code: string; number: string }[]
    stuckInSetup: number
    reportedNotes?: { courseId: number; code: string; notes: number }[]
  }
  recentUploads: { at: string; fileName: string; by: string; added: number; updated: number; removed: number }[]
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

export default function AdminHome() {
  const { semester, isOwner } = useAdmin()
  const [stats, setStats] = useState<Stats | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    api.get<Stats>(`/admin/stats${semester ? `?semesterId=${semester.id}` : ''}`).then(setStats, (e) => setError(errorText(e)))
  }, [semester])

  if (error) return <ErrorText>{error}</ErrorText>
  if (!stats)
    return (
      <div aria-busy="true" aria-label="Loading" className="flex flex-col gap-8">
        <Sk className="h-16 w-72 rounded-2xl" />
        <Sk className="h-28 rounded-2xl" />
        <Loading rows={3} />
      </div>
    )

  const shown = semester ?? stats.semester
  const live = stats.semester && shown?.id === stats.semester.id
  const numbers = [
    { value: stats.studentsRegistered, label: 'students signed in' },
    { value: stats.studentsFinished, label: stats.semester ? 'finished setup' : 'finished setup (nothing published)' },
    { value: stats.courses, label: `courses, ${plural(stats.sections, 'section')}` },
    { value: stats.sectionEditsThisWeek, label: 'section edits this week' },
  ]
  const { removed, noRoom, stuckInSetup, reportedNotes = [] } = stats.attention
  const attention: { to?: string; title: string; sub: string; action: string }[] = [
    ...reportedNotes.map((r) => ({
      to: `/admin/courses/${r.courseId}`,
      title: `${plural(r.notes, 'public note')} reported on ${r.code}`,
      sub: 'Students flagged them. Hide or delete if needed',
      action: 'Review',
    })),
    ...removed.map((r) => ({
      to: `/admin/courses/${r.courseId}`,
      title: `A ${r.code} section was removed`,
      sub: `${plural(r.students, 'student')} need${r.students === 1 ? 's' : ''} to pick again`,
      action: 'Open',
    })),
    ...(noRoom.length > 0
      ? [
          {
            to: `/admin/courses/${noRoom[0].courseId}`,
            title: `${plural(noRoom.length, 'section has', 'sections have')} no room`,
            sub:
              noRoom
                .slice(0, 6)
                .map((s) => `${s.code} ${s.number}`)
                .join(', ') + (noRoom.length > 6 ? ` and ${noRoom.length - 6} more` : ''),
            action: 'Fix',
          },
        ]
      : []),
    ...(stuckInSetup > 0
      ? [{ to: isOwner ? '/admin/students?status=pending' : undefined, title: `${plural(stuckInSetup, 'student')} stuck in setup`, sub: 'Signed in, never finished', action: 'See' }]
      : []),
  ]

  return (
    <>
      <PageTitle
        size={72}
        kicker={live && stats.semester?.publishedAt ? `Published ${shortDate(stats.semester.publishedAt)}` : shown ? (shown.publishedAt ? 'Not live' : 'Draft, not published') : 'No semester yet'}
        title={shown?.name ?? 'Welcome'}
        action={
          <Link to="/admin/upload" className="press flex h-12 items-center rounded-full bg-navy px-[22px] text-[16px] font-extrabold text-cream">
            Upload a new file
          </Link>
        }
      />
      {!stats.semester && (
        <Notice>
          Students can sign in and pick their major, but they’ll wait until you{' '}
          <Link className="underline" to="/admin/upload">
            upload and publish
          </Link>{' '}
          a semester.
        </Notice>
      )}
      <dl className="grid grid-cols-2 border-t-[2.5px] border-b-[1.5px] border-t-navy border-b-navy/[0.12] lg:grid-cols-4">
        {numbers.map((n, i) => (
          <div key={i} className={`flex flex-col-reverse gap-1.5 py-5 ${i % 2 ? 'border-l-[1.5px] border-navy/[0.12] pl-5' : 'pr-5'} lg:px-5 ${i > 0 ? 'lg:border-l-[1.5px] lg:border-navy/[0.12]' : 'lg:pl-0'} ${i > 1 ? 'max-lg:border-t-[1.5px] max-lg:border-navy/[0.12]' : ''}`}>
            <dt className="text-[14px] font-bold text-muted">{n.label}</dt>
            <dd className="display text-[52px] leading-[0.9] tracking-[-0.04em] tabular-nums">{n.value}</dd>
          </div>
        ))}
      </dl>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(min(320px,100%),1fr))] gap-10">
        <section className="flex flex-col">
          <h2 className="mb-2.5 text-[24px] font-extrabold tracking-[-0.02em]">Needs attention</h2>
          {attention.length === 0 ? (
            <p className="border-y-[1.5px] border-navy/[0.12] px-2 py-3.5 text-[16px] font-semibold text-muted">Nothing right now.</p>
          ) : (
            <div className="border-b-[1.5px] border-navy/[0.12]">
              {attention.map((a, i) => (
                <Row key={i} to={a.to} action={a.action}>
                  <span className="text-[16px] font-extrabold">{a.title}</span>
                  <span className="text-[14px] font-semibold text-muted">{a.sub}</span>
                </Row>
              ))}
            </div>
          )}
        </section>
        <section className="flex flex-col">
          <h2 className="mb-2.5 text-[24px] font-extrabold tracking-[-0.02em]">Recent uploads</h2>
          {stats.recentUploads.length === 0 ? (
            <p className="border-y-[1.5px] border-navy/[0.12] px-2 py-3.5 text-[16px] font-semibold text-muted">No uploads for this semester yet.</p>
          ) : (
            <div className="border-b-[1.5px] border-navy/[0.12]">
              {stats.recentUploads.map((u, i) => (
                <div key={i} className="grid grid-cols-[64px_minmax(0,1fr)_auto] items-baseline gap-3 border-t-[1.5px] border-navy/[0.12] px-2 py-3.5">
                  <span className="label text-[12px] tracking-normal">{shortDate(u.at)}</span>
                  <span className="flex min-w-0 flex-col gap-0.5">
                    <span className="text-[15px] font-extrabold [overflow-wrap:anywhere]">{u.fileName}</span>
                    <span className="text-[13px] font-semibold text-muted">by {u.by}</span>
                  </span>
                  <span className="text-[14px] font-bold">{uploadSummary(u)}</span>
                </div>
              ))}
            </div>
          )}
        </section>
      </div>
    </>
  )
}

function uploadSummary(u: Stats['recentUploads'][number]) {
  const parts = [u.added && `${u.added} added`, u.updated && `${u.updated} changed`, u.removed && `${u.removed} removed`].filter(Boolean)
  return parts.length ? parts.join(', ') : 'No changes'
}

function Row({ to, action, children }: { to?: string; action: string; children: ReactNode }) {
  const inner = (
    <>
      <span className="flex min-w-0 flex-col gap-0.5">{children}</span>
      {to && <span className="label shrink-0 text-[12px] tracking-normal">{action} →</span>}
    </>
  )
  const cls = 'flex items-center justify-between gap-4 border-t-[1.5px] border-navy/[0.12] px-2 py-3.5'
  return to ? (
    <Link to={to} className={`tint ${cls}`}>
      {inner}
    </Link>
  ) : (
    <div className={cls}>{inner}</div>
  )
}
