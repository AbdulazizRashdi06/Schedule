import PublicNotes from './PublicNotes'
import { PageTitle, useAdmin } from './AdminLayout'

/** Every public note students wrote on classes this semester, newest first. Private notes never show here. */
export default function Notes() {
  const { semester } = useAdmin()
  return (
    <>
      <PageTitle title="Notes." kicker={semester?.name} sub="Public notes students wrote on classes, newest first. Hide one to take it off students’ screens." />
      {semester && <PublicNotes semesterId={semester.id} />}
    </>
  )
}
