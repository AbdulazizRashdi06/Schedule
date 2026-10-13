import { useCallback, useEffect, useState } from 'react'
import { NavLink, Outlet, useNavigate, useOutletContext } from 'react-router-dom'
import { api, store } from '../lib/api'
import { useSession } from '../lib/session'
import type { Semester } from '../lib/types'
import { Select, ThemeToggle, cx } from '../components/ui'

export interface AdminCtx {
  semesters: Semester[]
  semester: Semester | null
  setSemesterId: (id: number) => void
  reloadSemesters: () => Promise<Semester[]>
  isOwner: boolean
}

export const useAdmin = () => useOutletContext<AdminCtx>()

export default function AdminLayout() {
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const isOwner = session?.role === 'owner'
  const [semesters, setSemesters] = useState<Semester[]>([])
  const [semesterId, setId] = useState<number | null>(() => Number(store.get('admin-semester')) || null)

  const reloadSemesters = useCallback(async () => {
    const list = await api.get<Semester[]>('/admin/semesters')
    setSemesters(list)
    setId((id) => (list.some((s) => s.id === id) ? id : (list.find((s) => s.isCurrent) ?? list[0])?.id ?? null))
    return list
  }, [])

  useEffect(() => {
    reloadSemesters().catch(() => {})
  }, [reloadSemesters])

  const setSemesterId = (id: number) => {
    setId(id)
    store.set('admin-semester', String(id))
  }
  const semester = semesters.find((s) => s.id === semesterId) ?? null

  const tabs = [
    { to: '/admin', label: 'Home', end: true },
    { to: '/admin/upload', label: 'Upload' },
    { to: '/admin/courses', label: 'Courses & sections' },
    { to: '/admin/majors', label: 'Majors' },
    ...(isOwner ? [{ to: '/admin/students', label: 'Students' }] : []),
    { to: '/admin/accounts', label: isOwner ? 'Admins' : 'Accounts' },
  ]

  return (
    <div className="min-h-dvh">
      <header className="sticky top-0 z-30 border-b border-zinc-200 bg-white/90 backdrop-blur dark:border-zinc-800 dark:bg-zinc-950/90">
        <div className="mx-auto flex h-14 max-w-5xl items-center gap-3 px-4">
          <div className="flex size-8 items-center justify-center rounded-lg bg-indigo-600 text-xs font-bold text-white">US</div>
          <span className="hidden font-semibold sm:inline">Dashboard</span>
          <Select
            aria-label="Semester"
            className="ml-1 h-9 max-w-44 text-sm"
            value={semesterId ?? ''}
            onChange={(e) => setSemesterId(Number(e.target.value))}
          >
            {semesters.length === 0 && <option value="">No semesters</option>}
            {semesters.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
                {s.isCurrent ? ' · live' : ''}
              </option>
            ))}
          </Select>
          <div className="ml-auto flex items-center gap-1">
            <span className="hidden text-sm text-zinc-500 md:inline">
              {session?.name} · {isOwner ? 'Owner' : 'RSA'}
            </span>
            <ThemeToggle />
            {session?.kind === 'student' && (
              <button onClick={() => navigate('/')} className="rounded-lg px-2.5 py-1.5 text-sm text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800">
                My timetable
              </button>
            )}
            <button
              onClick={async () => {
                const wasAdmin = session?.kind === 'admin'
                await logout()
                navigate(wasAdmin ? '/admin/login' : '/login', { replace: true })
              }}
              className="rounded-lg px-2.5 py-1.5 text-sm text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800"
            >
              Log out
            </button>
          </div>
        </div>
        <nav className="mx-auto flex max-w-5xl gap-1 overflow-x-auto px-3 pb-2">
          {tabs.map((t) => (
            <NavLink
              key={t.to}
              to={t.to}
              end={t.end}
              className={({ isActive }) =>
                cx('shrink-0 rounded-lg px-3 py-1.5 text-sm font-medium transition', isActive ? 'bg-zinc-900 text-white dark:bg-white dark:text-zinc-900' : 'text-zinc-600 hover:bg-zinc-100 dark:text-zinc-400 dark:hover:bg-zinc-800')
              }
            >
              {t.label}
            </NavLink>
          ))}
        </nav>
      </header>
      <main className="mx-auto max-w-5xl px-4 py-6">
        <Outlet context={{ semesters, semester, setSemesterId, reloadSemesters, isOwner } satisfies AdminCtx} />
      </main>
    </div>
  )
}

export function PageTitle({ title, sub, action }: { title: string; sub?: React.ReactNode; action?: React.ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {sub && <p className="mt-0.5 text-sm text-zinc-500">{sub}</p>}
      </div>
      {action}
    </div>
  )
}
