import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { store } from '../lib/api'
import { useSession } from '../lib/session'
import type { Me } from '../lib/types'
import { Sheet, Tag, cx } from '../components/ui'

const THEMES = [
  { id: 'light', label: 'Light' },
  { id: 'dark', label: 'Dark' },
  { id: 'auto', label: 'Auto' },
] as const

/** Menu / profile sheet: who you are, where to go, theme, log out. */
export function MenuSheet({ open, onClose, me }: { open: boolean; onClose: () => void; me: Me | null }) {
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const staff = session?.role === 'owner'
  const item = 'tint flex h-14 items-center justify-between rounded-[12px] text-[22px] font-extrabold'
  return (
    <Sheet open={open} onClose={onClose} label="Menu">
      <div className="flex flex-col gap-1 border-b-[1.5px] border-navy/10 pt-1.5 pb-4">
        <span className="font-mono text-[30px] font-bold tracking-[0.02em]">{me?.universityId ?? session?.name}</span>
        {me?.major && (
          <span className="text-[16px] font-bold">
            {me.major.name}
            {me.year != null && `, Year ${me.year}`}
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
      <ThemeSwitch />
      <button
        type="button"
        className="tint mt-4 flex h-14 items-center rounded-[12px] text-left text-[18px] font-extrabold text-alert"
        onClick={async () => {
          await logout()
          navigate('/login', { replace: true })
        }}
      >
        Log out
      </button>
    </Sheet>
  )
}

/** Light / Dark / Auto with a sliding indicator. Only the light palette is designed so far, so the choice is saved but not applied yet. */
function ThemeSwitch() {
  const [theme, setTheme] = useState(() => store.get('theme') ?? 'light')
  const index = Math.max(0, THEMES.findIndex((t) => t.id === theme))
  return (
    <div className="flex flex-col gap-2.5 border-t-[1.5px] border-navy/10 pt-3.5">
      <span id="theme-label" className="label text-[11px] tracking-[0.12em]">
        Theme
      </span>
      <div role="radiogroup" aria-labelledby="theme-label" className="relative grid grid-cols-3 rounded-full bg-navy/[0.07] p-1">
        <span aria-hidden="true" className="thumb absolute inset-y-1 left-1 w-[calc((100%-8px)/3)] rounded-full bg-navy" style={{ transform: `translateX(${index * 100}%)` }} />
        {THEMES.map((t) => (
          <button
            key={t.id}
            type="button"
            role="radio"
            aria-checked={t.id === theme}
            onClick={() => {
              setTheme(t.id)
              store.set('theme', t.id)
            }}
            className={cx('relative h-11 rounded-full text-[15px] font-extrabold transition-colors duration-200', t.id === theme ? 'text-cream' : 'text-navy')}
          >
            {t.label}
          </button>
        ))}
      </div>
      {theme !== 'light' && (
        <p className="msg text-[13px] font-semibold text-muted" aria-live="polite">
          Dark colours are on the way. Until then everything stays light.
        </p>
      )}
    </div>
  )
}
