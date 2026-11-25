import { useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../lib/api'
import { useHome } from '../lib/viewer'
import { viewAs } from '../lib/viewAs'
import { cx } from '../components/ui'

/** One line of the banner, in the shape of the student home's "No section picked" bar. */
function Bar({ tone = 'navy', children, action }: { tone?: 'navy' | 'ice' | 'alert'; children: ReactNode; action?: ReactNode }) {
  return (
    <div
      className={cx(
        'flex items-center gap-3 rounded-[18px] px-4 py-3 text-[14px] leading-snug font-semibold',
        tone === 'navy' ? 'bg-navy text-cream' : tone === 'ice' ? 'bg-ice text-navy' : 'bg-alert-bg text-alert-ink',
      )}
    >
      <span className="min-w-0 flex-1">{children}</span>
      {action}
    </div>
  )
}

/**
 * What a teacher needs to know before their day, above it: who an owner is viewing as, a name waiting for staff, a
 * freeze, classes the university removed, and their notices (a name approved, a class someone else changed).
 */
export default function TeachBanner() {
  const { teach, tt, retry } = useHome()
  const navigate = useNavigate()
  const [hidden, setHidden] = useState<number[]>([])
  if (!teach) return null

  const waiting = teach.kind === 'staff' && teach.status === 'pending' ? teach.names.find((n) => n.state === 'pending')?.name : undefined
  const notices = teach.notices.filter((n) => !hidden.includes(n.id))
  const removed = tt?.removed ?? []
  const button = 'label press flex min-h-11 shrink-0 items-center text-[10px] underline underline-offset-[3px]'

  const gotIt = (ids: number[]) => {
    // Gone at once; the next load of the home confirms it.
    setHidden((h) => [...h, ...ids])
    api.post('/teach/notices/seen', { ids }).then(
      () => void retry(),
      () => setHidden((h) => h.filter((id) => !ids.includes(id))),
    )
  }

  const bars: ReactNode[] = []
  if (teach.kind === 'view')
    bars.push(
      <Bar
        key="as"
        action={
          <button
            type="button"
            className={button}
            onClick={() => {
              viewAs.clear()
              navigate('/admin/teachers')
            }}
          >
            Exit
          </button>
        }
      >
        Viewing as {teach.as ?? teach.name}. Changes are logged under you.
      </Bar>,
    )
  if (waiting)
    bars.push(
      <Bar key="waiting" tone="ice">
        Waiting for staff to confirm you are {waiting}. You can add your classes meanwhile.
      </Bar>,
    )
  if (teach.frozen)
    bars.push(
      <Bar key="frozen" tone="alert">
        Your class changes are paused. Ask the Classy team.
      </Bar>,
    )
  else if (!teach.changesEnabled)
    bars.push(
      <Bar key="off" tone="alert">
        Class changes are switched off for everyone at the moment.
      </Bar>,
    )
  if (removed.length > 0)
    bars.push(
      <Bar key="removed" tone="ice">
        The university removed {removed.map((r) => `${r.code} section ${r.number}`).join(', ')}.
      </Bar>,
    )
  for (const n of notices) {
    const text = (
      <>
        <span className="font-extrabold">{n.title}</span>
        {n.body && <span className="block">{n.body}</span>}
      </>
    )
    bars.push(
      <Bar
        key={n.id}
        tone="ice"
        // An owner looking around never clears the teacher's inbox.
        action={
          teach.personal && (
            <button type="button" className={button} onClick={() => gotIt([n.id])}>
              Got it
            </button>
          )
        }
      >
        {n.url ? (
          <Link to={n.url} className="block">
            {text}
          </Link>
        ) : (
          text
        )}
      </Bar>,
    )
  }
  if (bars.length === 0) return null
  return <div className="mb-5 flex flex-col gap-2">{bars}</div>
}
