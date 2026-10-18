import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useSession } from '../lib/session'
import type { Me } from '../lib/types'
import { yearName } from '../lib/year'
import { Sheet, Tag, cx } from '../components/ui'
import LogoutConfirm from '../components/LogoutConfirm'
import ChangeYear from './ChangeYear'

/** Menu / profile sheet: who you are, where to go, colours, log out. */
export function MenuSheet({ open, onClose, me }: { open: boolean; onClose: () => void; me: Me | null }) {
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const staff = session?.role === 'owner'
  const [confirmOut, setConfirmOut] = useState(false)
  const [changingYear, setChangingYear] = useState(false)
  const item = 'tint flex h-14 items-center justify-between rounded-[12px] text-[22px] font-extrabold'
  return (
    <>
      <Sheet open={open} onClose={onClose} label="Menu">
      <div className="flex flex-col gap-1 border-b-[1.5px] border-navy/10 pt-1.5 pb-4">
        <span className="font-mono text-[30px] font-bold tracking-[0.02em]">{me?.universityId ?? session?.name}</span>
        {me?.major && (
          <span className="text-[16px] font-bold">
            {me.major.name}
            {me.year != null && `, ${yearName(me.year)}`}
          </span>
        )}
        {me?.email && <span className="text-[14px] font-semibold break-all text-muted">{me.email}</span>}
      </div>
      <nav aria-label="Menu" className="flex flex-col">
        {me?.setupDone && (
          <>
            <Link to="/" onClick={onClose} className={item}>
              Today<span aria-hidden="true" className="text-[18px]">→</span>
            </Link>
            <Link to="/schedule" onClick={onClose} className={item}>
              Full week<span aria-hidden="true" className="text-[18px]">→</span>
            </Link>
            <Link to="/edit" onClick={onClose} className={item}>
              Edit my courses<span aria-hidden="true" className="text-[18px]">→</span>
            </Link>
            <button
              type="button"
              onClick={() => {
                onClose()
                setChangingYear(true)
              }}
              className={cx(item, 'text-left')}
            >
              Change my year<span aria-hidden="true" className="text-[18px]">→</span>
            </button>
          </>
        )}
        {staff && (
          <Link to="/admin" className={item}>
            <span className="flex items-center gap-2.5">
              Admin <Tag>Staff</Tag>
            </span>
            <span aria-hidden="true" className="text-[18px]">→</span>
          </Link>
        )}
      </nav>
      <Link to="/colours" onClick={onClose} className="tint flex h-14 items-center justify-between rounded-[12px] border-t-[1.5px] border-navy/10 text-[18px] font-extrabold">
        <span className="flex items-center gap-2.5">
          <span aria-hidden="true" className="flex overflow-hidden rounded-full">
            <span className="size-5 bg-navy" />
            <span className="size-5 bg-sky" />
          </span>
          Colours
        </span>
        <span aria-hidden="true" className="text-[18px]">
          →
        </span>
      </Link>
      <button
        type="button"
        className="tint mt-4 flex h-14 items-center rounded-[12px] text-left text-[18px] font-extrabold text-alert"
        onClick={() => setConfirmOut(true)}
      >
        Log out
      </button>
      </Sheet>
      {me && <ChangeYear key={String(changingYear)} open={changingYear} onClose={() => setChangingYear(false)} current={me.year} />}
      <LogoutConfirm
        open={confirmOut}
        onClose={() => setConfirmOut(false)}
        onLogout={async () => {
          await logout()
          navigate('/login', { replace: true })
        }}
      />
    </>
  )
}
