import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Tag } from '../components/ui'
import { Th } from './AdminLayout'

interface PublicNote {
  id: number
  author: string
  text: string
  date: string | null
  section: string | null
  reports: number
  hidden: boolean
  createdAt: string
}

/** Public notes students wrote on this course: staff can hide them (students stop seeing them) or delete them. Private notes never show here. */
export default function PublicNotes({ courseId }: { courseId: number }) {
  const [notes, setNotes] = useState<PublicNote[] | null>(null)
  const [error, setError] = useState('')
  const load = () => api.get<PublicNote[]>(`/admin/courses/${courseId}/notes`).then(setNotes, (e) => setError(errorText(e)))
  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [courseId])

  const act = async (fn: () => Promise<unknown>) => {
    setError('')
    try {
      await fn()
      await load()
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <section className="flex flex-col gap-2.5">
      <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">Public notes</h2>
      <ErrorText>{error}</ErrorText>
      {notes && notes.length === 0 && <p className="text-[15px] font-semibold text-muted">No public notes on this course.</p>}
      {notes && notes.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[640px] border-collapse text-[15px]">
            <thead>
              <tr className="border-b-[2.5px] border-navy">
                <Th>Student</Th>
                <Th>Note</Th>
                <Th>For</Th>
                <Th right>Reports</Th>
                <th scope="col">
                  <span className="sr">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {notes.map((n) => (
                <tr key={n.id} className="border-b-[1.5px] border-navy/10 align-top">
                  <td className="px-2 py-3 font-mono font-bold">{n.author}</td>
                  <td className="max-w-[380px] px-2 py-3 font-semibold break-words whitespace-pre-wrap">
                    {n.hidden && (
                      <Tag tone="plain" className="mr-1.5">
                        Hidden
                      </Tag>
                    )}
                    {n.text}
                  </td>
                  <td className="px-2 py-3 font-semibold whitespace-nowrap">{n.date ? `${n.date} · Sec ${n.section}` : 'Whole class'}</td>
                  <td className="px-2 py-3 text-right font-bold tabular-nums">{n.reports > 0 ? <Tag tone="alert">{n.reports}</Tag> : 0}</td>
                  <td className="px-2 py-3 text-right whitespace-nowrap">
                    <Button variant="secondary" size="sm" className="mr-1.5" onClick={() => act(() => api.put(`/admin/notes/${n.id}/hidden`, { hidden: !n.hidden }))}>
                      {n.hidden ? 'Show' : 'Hide'}
                    </Button>
                    <Button variant="danger" size="sm" onClick={() => act(() => api.del(`/admin/notes/${n.id}`))}>
                      Delete
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
