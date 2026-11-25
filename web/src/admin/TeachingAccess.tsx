import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Button, Checkbox, ErrorText, TextField } from '../components/ui'

interface Teaching {
  teacherId: number
  displayName: string
  frozen?: boolean
  /** Their live changes that can still be undone. */
  upcomingChanges?: number
}

/**
 * Owners only: gives a student account teaching rights (a teaching assistant). They keep signing in as a student and
 * get the teacher side from their menu. Timetable names are attached on the Teachers page, as for anyone.
 */
export default function TeachingAccess({ studentId, universityId }: { studentId: number; universityId: string }) {
  const [teaching, setTeaching] = useState<Teaching | null>(null)
  const [name, setName] = useState('')
  const [undo, setUndo] = useState(false)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  useEffect(() => {
    let active = true
    api.get<{ teaching: Teaching | null }>(`/admin/students/${studentId}/teaching`).then(
      (r) => {
        if (!active) return
        setTeaching(r.teaching)
        setName(r.teaching?.displayName ?? '')
        setLoading(false)
      },
      (e) => {
        if (!active) return
        setError(errorText(e))
        setLoading(false)
      },
    )
    return () => {
      active = false
    }
  }, [studentId])

  const run = async (what: string, fn: () => Promise<void>) => {
    setBusy(what)
    setError('')
    setNotice('')
    try {
      await fn()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy('')
    }
  }
  const upcoming = teaching?.upcomingChanges ?? 0
  const changes = `${upcoming} upcoming ${upcoming === 1 ? 'change' : 'changes'}`

  return (
    <section className="flex max-w-xl flex-col gap-3 rounded-[18px] border border-navy/20 p-5" aria-label="Teaching rights">
      <h2 className="text-[22px] font-extrabold">Teaching rights</h2>
      <p className="text-[14px] font-semibold text-muted">
        {teaching ? `${universityId} teaches as ${teaching.displayName}${teaching.frozen ? ' (frozen)' : ''}.` : 'This student has no teaching rights.'} A teaching assistant keeps their student sign-in, gets a Teaching item in their menu and can
        change classes like a teacher. Attach their timetable names on the{' '}
        <Link to="/admin/teachers" className="font-bold text-navy underline underline-offset-2">
          Teachers page
        </Link>
        .
      </p>
      <TextField label="Name students see" placeholder="Sara Al Balushi" maxLength={80} disabled={loading || !!busy} value={name} onChange={(e) => setName(e.target.value)} />
      {teaching && upcoming > 0 && (
        <label className="flex cursor-pointer items-start gap-3 text-[14px] leading-snug font-bold">
          <Checkbox checked={undo} onChange={setUndo} label={`When revoking, also undo their ${changes}`} />
          <span>
            When revoking, also undo their {changes} <span className="font-semibold text-muted">(students are told the classes are back on)</span>
          </span>
        </label>
      )}
      <ErrorText>{error}</ErrorText>
      {notice && (
        <p role="status" className="text-[14px] font-bold">
          {notice}
        </p>
      )}
      <div className="flex flex-wrap gap-2">
        <Button
          disabled={loading || !name.trim() || (!!teaching && name.trim() === teaching.displayName) || (!!busy && busy !== 'save')}
          busy={busy === 'save'}
          onClick={() =>
            run('save', async () => {
              const had = !!teaching
              const r = await api.put<{ teaching: Teaching }>(`/admin/students/${studentId}/teaching`, { displayName: name.trim() })
              setTeaching({ ...teaching, ...r.teaching })
              setName(r.teaching.displayName)
              setNotice(had ? 'Name saved.' : 'Teaching rights granted. They enter a new sign-in code once before their first class change.')
            })
          }
        >
          {teaching ? 'Save name' : 'Grant teaching rights'}
        </Button>
        {teaching && (
          <Button
            variant="danger"
            disabled={!!busy && busy !== 'revoke'}
            busy={busy === 'revoke'}
            onClick={() =>
              run('revoke', async () => {
                const r = await api.del<{ undone: number }>(`/admin/students/${studentId}/teaching${undo ? '?undoChanges=true' : ''}`)
                setTeaching(null)
                setName('')
                setUndo(false)
                setNotice(r.undone ? `Teaching rights revoked. ${r.undone} ${r.undone === 1 ? 'change' : 'changes'} undone.` : 'Teaching rights revoked immediately.')
              })
            }
          >
            Revoke teaching rights
          </Button>
        )}
      </div>
    </section>
  )
}
