import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Select } from '../components/ui'

export default function BatchRepAccess({ studentId, currentMajorId }: { studentId: number; currentMajorId?: number }) {
  const [majors, setMajors] = useState<{ id: number; code: string; name: string; family: string | null }[]>([])
  const [homeMajor, setHomeMajor] = useState(currentMajorId ?? 0), [assignment, setAssignment] = useState<{ homeFamily: string } | null>(null)
  const [loading, setLoading] = useState(true), [busy, setBusy] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState('')
  useEffect(() => {
    let active = true
    Promise.all([api.get<typeof majors>('/admin/majors'), api.get<{ assignment: typeof assignment }>(`/admin/students/${studentId}/batch-rep`)]).then(([list, value]) => {
      if (!active) return
      setMajors(list); setAssignment(value.assignment)
      const existing = list.find(m => (m.family ? `family:${m.family.trim().toUpperCase()}` : `major:${m.id}`) === value.assignment?.homeFamily)
      setHomeMajor(existing?.id ?? currentMajorId ?? list[0]?.id ?? 0); setLoading(false)
    }, e => { if (active) { setError(errorText(e)); setLoading(false) } })
    return () => { active = false }
  }, [studentId, currentMajorId])
  return <section className="flex max-w-xl flex-col gap-3 rounded-[18px] border border-navy/20 p-5" aria-label="Batch representative access"><h2 className="text-[22px] font-extrabold">Batch representative</h2><p className="text-[14px] font-semibold text-muted">{assignment ? `Assigned to ${assignment.homeFamily.replace('family:', '').replace('major:', 'major ')}.` : 'This student is not a batch representative.'} Reps in the same family share their semester workspace and can add any major or course.</p><label className="flex flex-col gap-1.5 text-[13px] font-bold">Home major / family<Select disabled={loading || busy} value={homeMajor} onChange={e => setHomeMajor(Number(e.target.value))}>{majors.map(m => <option key={m.id} value={m.id}>{m.code} · {m.family ?? 'Own major workspace'}</option>)}</Select></label><ErrorText>{error}</ErrorText>{notice && <p role="status" className="text-[14px] font-bold">{notice}</p>}<div className="flex flex-wrap gap-2"><Button disabled={loading || !homeMajor} busy={busy} onClick={async () => { setBusy(true); setError(''); try { const result = await api.put<{ homeFamily: string }>(`/admin/students/${studentId}/batch-rep`, { homeMajorId: homeMajor }); setAssignment(result); setNotice('Rep access saved. The role takes effect on the next request.') } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>{assignment ? 'Update home workspace' : 'Grant batch rep access'}</Button>{assignment && <Button variant="danger" disabled={busy} onClick={async () => { setBusy(true); setError(''); try { await api.del(`/admin/students/${studentId}/batch-rep`); setAssignment(null); setNotice('Rep access revoked immediately.') } catch (e) { setError(errorText(e)) } finally { setBusy(false) } }}>Revoke rep access</Button>}</div></section>
}
