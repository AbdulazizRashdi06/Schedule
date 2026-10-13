import { Link, useNavigate } from 'react-router-dom'
import { Tag } from '../components/ui'
import { BackButton } from './timetable'

/** Not found: a course link that isn't in the timetable, or a page that doesn't exist. */
export default function NotFound({ tag, course }: { tag?: string; course?: boolean }) {
  const navigate = useNavigate()
  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        <header>
          <BackButton label="Back to today" onClick={() => navigate('/')} />
        </header>
        <div className="flex flex-col gap-3.5 pt-14">
          {tag && <Tag className="self-start px-2.5 py-1.5 text-[13px] tracking-[0.12em]">{tag}</Tag>}
          <h1 className="display rise text-[64px] leading-[0.86] tracking-[-0.05em]">{course ? 'Not in your timetable.' : 'Nothing here.'}</h1>
          <p className="rise text-[17px] leading-[1.4] font-semibold" style={{ animationDelay: '60ms' }}>
            {course ? 'This link points to a course you haven’t picked, or it was removed this semester.' : 'This page doesn’t exist. The link may be old or mistyped.'}
          </p>
        </div>
        <div className="mt-auto flex flex-col gap-2.5 pt-10">
          <Link to="/" className="press flex h-[60px] items-center justify-center rounded-full bg-navy text-[18px] font-extrabold text-cream">
            Back to today
          </Link>
          {course && (
            <Link to="/edit" className="label flex h-12 items-center justify-center text-[12px] link-hover">
              Add it to my courses
            </Link>
          )}
        </div>
      </div>
    </div>
  )
}
