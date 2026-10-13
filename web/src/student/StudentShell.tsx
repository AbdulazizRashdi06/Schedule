import { useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSession } from '../lib/session'
import { Sheet, ThemeToggle } from '../components/ui'

/** Phone-width page with a header: title on the left, menu on the right. */
export function StudentShell({ title, children, menu = true, setupDone }: { title?: ReactNode; children: ReactNode; menu?: boolean; setupDone?: boolean }) {
  return (
    <div className="mx-auto min-h-dvh max-w-xl px-5 pt-[max(0.5rem,env(safe-area-inset-top))]">
      <header className="flex h-14 items-center justify-between gap-3">
        <div className="min-w-0 flex-1">{title}</div>
        <div className="flex items-center">
          <ThemeToggle />
          {menu && <Menu setupDone={setupDone} />}
        </div>
      </header>
      {children}
    </div>
  )
}

function Menu({ setupDone }: { setupDone?: boolean }) {
  const [open, setOpen] = useState(false)
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const item = 'flex w-full items-center rounded-xl px-3 py-3 text-left text-[15px] font-medium hover:bg-zinc-100 dark:hover:bg-zinc-800'
  return (
    <>
      <button onClick={() => setOpen(true)} className="rounded-lg p-2 text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800" aria-label="Menu">
        <svg viewBox="0 0 20 20" className="size-5" fill="none" stroke="currentColor" strokeWidth="1.8">
          <path d="M3 6h14M3 10h14M3 14h14" strokeLinecap="round" />
        </svg>
      </button>
      <Sheet open={open} onClose={() => setOpen(false)} title={session?.name}>
        <nav className="-mx-2 space-y-1">
          {setupDone && (
            <>
              <button className={item} onClick={() => navigate('/')}>My timetable</button>
              <button className={item} onClick={() => navigate('/edit')}>Edit my courses</button>
            </>
          )}
          {session?.role === 'owner' && (
            <button className={item} onClick={() => navigate('/admin')}>Admin dashboard</button>
          )}
          <button
            className={item + ' text-red-600 dark:text-red-400'}
            onClick={async () => {
              await logout()
              navigate('/login', { replace: true })
            }}
          >
            Log out
          </button>
        </nav>
      </Sheet>
    </>
  )
}
