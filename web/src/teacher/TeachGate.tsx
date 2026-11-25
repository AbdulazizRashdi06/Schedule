import { Navigate } from 'react-router-dom'
import { useSession } from '../lib/session'
import { TeacherScope, mayTeach } from '../lib/viewer'
import Lab from '../student/Lab'
import { TodaySkeleton } from '../student/Today'
import TeachLogin from './TeachLogin'

/**
 * /teach: the teacher's home for whoever may use the teacher side, and the teacher sign-in for everyone else. An
 * owner comes here through "view as" on the dashboard; without one there is nothing of theirs to show.
 */
export default function TeachGate() {
  const { session } = useSession()
  if (!session) return <TodaySkeleton />
  if (mayTeach(session))
    return (
      <TeacherScope>
        <Lab />
      </TeacherScope>
    )
  if (session.role === 'owner') return <Navigate to="/admin/teachers" replace />
  return <TeachLogin other={session.kind ? (session.name ?? 'someone else') : undefined} />
}
