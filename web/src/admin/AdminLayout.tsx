import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { Link, NavLink, Outlet, useNavigate, useOutletContext } from 'react-router-dom'
import { api, store } from '../lib/api'
import { useSession } from '../lib/session'
import type { Semester } from '../lib/types'
import { cx } from '../components/ui'

export interface AdminCtx {
  semesters: Semester[]
  semester: Semester | null
  setSemesterId: (id: number) => void
  reloadSemesters: () => Promise<Semester[]>
  isOwner: boolean
}

export const useAdmin = () => useOutletContext<AdminCtx>()

/** Dashboard shell: sidebar on the left (wraps above the content on narrow screens), page on the right. */
export default function AdminLayout() {
  const { session, logout } = useSession()
  const navigate = useNavigate()
  const isOwner = session?.role === 'owner'
  const [semesters, setSemesters] = useState<Semester[]>([])
  const [semesterId, setId] = useState<number | null>(() => Number(store.get('admin-semester')) || null)

  const reloadSemesters = useCallback(async () => {
    const list = await api.get<Semester[]>('/admin/semesters')
    setSemesters(list)
    setId((id) => (list.some((s) => s.id === id) ? id : ((list.find((s) => s.isCurrent) ?? list[0])?.id ?? null)))
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

  const link = ({ isActive }: { isActive: boolean }) =>
    cx('flex h-11 items-center rounded-[12px] px-3 text-[16px]', isActive ? 'bg-navy font-extrabold text-cream' : 'tint font-bold')

  return (
    <div className="flex min-h-dvh flex-wrap bg-cream text-navy">
      <aside className="flex max-w-full flex-[1_0_220px] flex-col gap-1 border-navy/10 px-4 py-7 max-[799px]:border-b-[1.5px] min-[800px]:border-r-[1.5px]">
        <span className="label px-3 pb-4 text-[12px] tracking-[0.14em]">Uni Schedule · Staff</span>
        {semesters.length > 0 && (
          <label className="flex flex-col gap-1 px-3 pb-4">
            <span className="label text-[10px] tracking-[0.14em] text-muted">Semester</span>
            <select
              value={semesterId ?? ''}
              onChange={(e) => setSemesterId(Number(e.target.value))}
              className="field h-10 rounded-[12px] bg-paper px-2.5 text-[15px] font-bold text-navy outline-none"
            >
              {semesters.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                  {s.isCurrent ? ' (live)' : s.publishedAt ? '' : ' (draft)'}
                </option>
              ))}
            </select>
          </label>
        )}
        <nav aria-label="Dashboard" className="flex flex-col gap-0.5">
          <NavLink to="/admin" end className={link}>
            Home
          </NavLink>
          <NavLink to="/admin/upload" className={link}>
            Upload
          </NavLink>
          <NavLink to="/admin/courses" className={link}>
            Courses
          </NavLink>
          <NavLink to="/admin/majors" className={link}>
            Majors
          </NavLink>
          {isOwner && <span className="label px-3 pt-[18px] pb-1.5 text-[10px] tracking-[0.14em] text-muted">Owners</span>}
          {isOwner && (
            <NavLink to="/admin/students" className={link}>
              Students
            </NavLink>
          )}
          <NavLink to="/admin/accounts" className={link}>
            Accounts
          </NavLink>
        </nav>
        <div className="mt-auto flex flex-col gap-1 border-t-[1.5px] border-navy/10 px-3 pt-4 max-[799px]:mt-4">
          <span className="text-[15px] font-extrabold break-all">{session?.name}</span>
          <span className="text-[13px] font-semibold text-muted">{isOwner ? 'Owner' : 'RSA'}</span>
          {session?.kind === 'student' && (
            <Link to="/" className="pt-2 text-[14px] font-bold link-hover">
              My timetable
            </Link>
          )}
          {session?.kind === 'admin' && (
            <Link to="/admin/password" className="pt-2 text-[14px] font-bold link-hover">
              Change password
            </Link>
          )}
          <button
            type="button"
            className="self-start pt-2 text-[14px] font-bold link-hover"
            onClick={async () => {
              const wasAdmin = session?.kind === 'admin'
              await logout()
              navigate(wasAdmin ? '/admin/login' : '/login', { replace: true })
            }}
          >
            Log out
          </button>
        </div>
      </aside>
      <main className="flex min-w-0 flex-[999_1_560px] flex-col gap-7 px-5 py-8 sm:px-10 sm:py-9">
        <Outlet context={{ semesters, semester, setSemesterId, reloadSemesters, isOwner } satisfies AdminCtx} />
      </main>
    </div>
  )
}

export function PageTitle({ title, kicker, sub, action, size = 56 }: { title: ReactNode; kicker?: ReactNode; sub?: ReactNode; action?: ReactNode; size?: number }) {
  return (
    <div className="flex flex-wrap items-end justify-between gap-4">
      <div className="flex min-w-0 flex-col gap-2">
        {kicker && <span className="label text-[12px] tracking-[0.12em]">{kicker}</span>}
        <h1 className="display leading-[0.9] tracking-[-0.05em] break-words" style={{ fontSize: size }}>
          {title}
        </h1>
        {sub && <span className="text-[16px] font-semibold text-muted">{sub}</span>}
      </div>
      {action}
    </div>
  )
}

/** Mono, uppercase table header cell. */
export function Th({ children, right }: { children?: ReactNode; right?: boolean }) {
  return (
    <th scope="col" className={cx('label px-2 py-2.5 text-[11px] tracking-[0.12em]', right ? 'text-right' : 'text-left')}>
      {children}
    </th>
  )
}

/** Filter pills: All, Needs fix, Changed… */
export function Filters<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <div role="group" aria-label="Filter" className="flex flex-wrap gap-1.5">
      {options.map(([id, label]) => (
        <button
          key={id}
          type="button"
          aria-pressed={value === id}
          onClick={() => onChange(id)}
          className={cx('press h-12 rounded-full px-4 text-[15px] font-extrabold', value === id ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.18)]')}
        >
          {label}
        </button>
      ))}
    </div>
  )
}

/** "9 Oct" */
export const shortDate = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })

/** "Today 09:52", "Yesterday", "8 Oct". */
export function seen(iso: string | null, never = 'Never') {
  if (!iso) return never
  const d = new Date(iso)
  const today = new Date()
  const days = Math.round((new Date(today.toDateString()).getTime() - new Date(d.toDateString()).getTime()) / 86_400_000)
  if (days === 0) return `Today ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
  if (days === 1) return 'Yesterday'
  return shortDate(iso)
}
