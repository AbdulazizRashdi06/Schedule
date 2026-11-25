import { useEffect, useState } from 'react'
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { useSession } from '../lib/session'
import { KIND_NAME, rememberInvite, type Group, type GroupKind } from '../lib/groups'
import { Button, ErrorText, Sk, TextField } from '../components/ui'

interface Invite {
  id: number
  name: string
  kind: GroupKind
  members: number
  joined: boolean
  full: boolean
}

/** Opening an invite link: sign in first if needed, then pick a name and join. */
export default function Join() {
  const { code = '' } = useParams()
  const { session } = useSession()
  const navigate = useNavigate()
  const [invite, setInvite] = useState<Invite | null>(null)
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  // Kept apart from the invite's own error, so a failed join leaves the form up to try again.
  const [joinError, setJoinError] = useState('')
  const student = session?.kind === 'student'

  useEffect(() => {
    if (!student) return
    api.get<Invite>(`/groups/invite/${code}`).then(
      (i) => (i.joined ? navigate(`/groups/${i.id}`, { replace: true }) : setInvite(i)),
      (e) => setError(errorText(e)),
    )
  }, [code, student, navigate])

  if (!session) return <div className="min-h-dvh bg-cream" aria-busy="true" />
  if (session.kind === 'admin') return <Navigate to="/admin" replace />
  // The invite isn't kept for later: a teacher has no groups to join.
  if (session.kind === 'teacher')
    return (
      <div className="min-h-dvh bg-cream text-navy">
        <div className="mx-auto flex max-w-[480px] flex-col gap-5 px-6 pt-[max(2.5rem,env(safe-area-inset-top))] pb-10">
          <span className="label text-[12px] tracking-[0.14em]">Classy · Invite</span>
          <h1 className="display text-[52px] leading-[0.86] tracking-[-0.05em]">Groups are for students.</h1>
          <p className="text-[16px] font-semibold text-muted">This is an invite to a student group. You’re signed in as a teacher.</p>
          <Link to="/teach" className="label self-start text-[12px] link-hover">
            Go to my classes
          </Link>
        </div>
      </div>
    )
  if (!student) {
    rememberInvite(code)
    return <Navigate to="/login" replace />
  }

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex max-w-[480px] flex-col gap-5 px-6 pt-[max(2.5rem,env(safe-area-inset-top))] pb-10">
        <span className="label text-[12px] tracking-[0.14em]">Classy · Invite</span>
        {error ? (
          <>
            <ErrorText>{error}</ErrorText>
            <Link to="/" className="label self-start text-[12px] link-hover">
              Go to Classy
            </Link>
          </>
        ) : !invite ? (
          <Sk className="h-[120px] rounded-[20px]" />
        ) : (
          <>
            <div className="flex flex-col gap-2">
              <h1 className="display text-[52px] leading-[0.86] tracking-[-0.05em] break-words">{invite.name}</h1>
              <p className="text-[16px] font-semibold text-muted">
                {KIND_NAME[invite.kind]} group · {invite.members} {invite.members === 1 ? 'person' : 'people'}.{' '}
                {invite.kind === 'car' ? 'Join to see when the car leaves each day.' : 'Join to see when you’re all free.'}
              </p>
            </div>
            {invite.full ? (
              <>
                <p className="text-[16px] font-bold">This group is full.</p>
                <Link to="/" className="label self-start text-[12px] link-hover">
                  Go to Classy
                </Link>
              </>
            ) : (
              <>
                <TextField label="Your name in the group" value={name} maxLength={30} placeholder="What your friends call you" onChange={(e) => setName(e.target.value)} />
                <p className="-mt-2 text-[13px] font-semibold text-muted">They see this name and when you’re busy, never your student ID. You can show your classes later if you like.</p>
                <ErrorText>{joinError}</ErrorText>
                <Button
                  busy={busy}
                  disabled={!name.trim()}
                  onClick={async () => {
                    setBusy(true)
                    setJoinError('')
                    try {
                      const g = await api.post<Group>(`/groups/invite/${code}/join`, { displayName: name })
                      navigate(`/groups/${g.id}`, { replace: true })
                    } catch (e) {
                      setJoinError(errorText(e))
                      setBusy(false)
                    }
                  }}
                >
                  Join group
                </Button>
              </>
            )}
          </>
        )}
      </div>
    </div>
  )
}
