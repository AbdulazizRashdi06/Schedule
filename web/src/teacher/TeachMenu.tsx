import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../lib/api'
import { useSession } from '../lib/session'
import type { TeachMe } from '../lib/types'
import { Confirm, Sheet, Tag, cx, useToast, Toast } from '../components/ui'
import LogoutConfirm from '../components/LogoutConfirm'
import CalendarSheet from '../student/CalendarSheet'
import NotificationsSheet from '../student/NotificationsSheet'

/** The teacher side's menu: who you are, your classes and details, the extras, colours, log out. */
export default function TeachMenu({ open, onClose, teach }: { open: boolean; onClose: () => void; teach: TeachMe | null }) {
  const { session, refresh, logout } = useSession()
  const navigate = useNavigate()
  const [confirmOut, setConfirmOut] = useState(false)
  const [confirmDevices, setConfirmDevices] = useState(false)
  const [calendar, setCalendar] = useState(false)
  const [notify, setNotify] = useState(false)
  const toast = useToast()
  const item = 'tint flex h-14 items-center justify-between rounded-[12px] text-[22px] font-extrabold'
  const arrow = <span aria-hidden="true" className="text-[18px]">→</span>
  const staff = teach?.kind === 'staff'
  // Own slots, devices and the calendar link belong to the teacher, not to an owner looking in.
  const personal = teach?.personal ?? false
  const sheet = (show: (v: boolean) => void) => () => {
    onClose()
    show(true)
  }
  return (
    <>
      <Sheet open={open} onClose={onClose} label="Menu">
        <div className="flex flex-col gap-1 border-b-[1.5px] border-navy/10 pt-1.5 pb-4">
          <span className="text-[30px] leading-[1.05] font-extrabold tracking-[-0.02em] break-words">{teach?.name ?? session?.teacher?.displayName ?? session?.name}</span>
          <span className="flex flex-wrap items-center gap-2 text-[14px] font-semibold text-muted">
            {teach?.kind === 'view' ? <Tag>Viewing as</Tag> : teach?.kind === 'student' ? <Tag>Teaching assistant</Tag> : <Tag>Teacher</Tag>}
            {teach?.email && personal && <span className="break-all">{teach.email}</span>}
          </span>
        </div>
        <nav aria-label="Menu" className="flex flex-col">
          <Link to="/teach/rooms" onClick={onClose} className={item}>
            Free rooms{arrow}
          </Link>
          <Link to="/teach/classes" onClick={onClose} className={item}>
            Edit my classes{arrow}
          </Link>
          <Link to="/teach/office" onClick={onClose} className={item}>
            Office hours &amp; details{arrow}
          </Link>
          {personal && (
            <>
              <button type="button" onClick={sheet(setCalendar)} className={cx(item, 'text-left')}>
                Calendar{arrow}
              </button>
              <button type="button" onClick={sheet(setNotify)} className={cx(item, 'text-left')}>
                Notifications{arrow}
              </button>
            </>
          )}
          {staff && teach.status !== 'linked' && (
            <Link to="/teach/name" onClick={onClose} className={item}>
              Pick my name{arrow}
            </Link>
          )}
          {staff && (
            <button type="button" onClick={sheet(setConfirmDevices)} className={cx(item, 'text-left')}>
              Sign out my other devices{arrow}
            </button>
          )}
          {teach?.kind === 'student' && (
            <Link to="/" onClick={onClose} className={item}>
              My student timetable{arrow}
            </Link>
          )}
          {session?.role === 'owner' && (
            <Link to="/admin" onClick={onClose} className={item}>
              <span className="flex items-center gap-2.5">
                Dashboard <Tag>Staff</Tag>
              </span>
              {arrow}
            </Link>
          )}
        </nav>
        <Link to="/teach/colours" onClick={onClose} className="tint flex h-14 items-center justify-between rounded-[12px] border-t-[1.5px] border-navy/10 text-[18px] font-extrabold">
          <span className="flex items-center gap-2.5">
            <span aria-hidden="true" className="flex overflow-hidden rounded-full">
              <span className="size-5 bg-navy" />
              <span className="size-5 bg-sky" />
            </span>
            Colours
          </span>
          {arrow}
        </Link>
        <button type="button" className="tint mt-4 flex h-14 items-center rounded-[12px] text-left text-[18px] font-extrabold text-alert" onClick={() => setConfirmOut(true)}>
          Log out
        </button>
      </Sheet>
      {personal && (
        <>
          <CalendarSheet open={calendar} onClose={() => setCalendar(false)} />
          <NotificationsSheet open={notify} onClose={() => setNotify(false)} />
        </>
      )}
      <Confirm
        open={confirmDevices}
        onClose={() => setConfirmDevices(false)}
        title="Sign out my other devices?"
        body="Every other phone and browser signed in as you is signed out. This one stays signed in."
        confirmLabel="Sign them out"
        onConfirm={async () => {
          await api.post('/teach/sign-out-everywhere')
          // This browser was given a new sign-in in the same step.
          await refresh()
          toast.show('Done. Your other devices are signed out.')
        }}
      />
      <Toast toast={toast} className="inset-x-6 bottom-6 mx-auto max-w-[432px] rounded-[20px] bg-ice px-[18px] py-4 text-[16px] font-extrabold shadow-[0_12px_24px_-14px_rgb(11_25_87/0.5)]" />
      <LogoutConfirm
        open={confirmOut}
        onClose={() => setConfirmOut(false)}
        onLogout={async () => {
          await logout()
          navigate('/teach', { replace: true })
        }}
      />
    </>
  )
}
