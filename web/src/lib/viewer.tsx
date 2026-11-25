import { createContext, useContext, useMemo, type ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import type { HomeData } from './homeStore'
import { useSession } from './session'
import type { SessionInfo } from './types'
import { viewAs } from './viewAs'
import { useTimetable } from '../student/timetable'
import { useTeachTimetable } from '../teacher/teachData'

/**
 * Who the home page is for. The same screens (home, a day, the week, a session) serve a student's own timetable and
 * the classes a teacher teaches; the viewer picks the data, the API base and the handful of places they differ.
 */
export interface Viewer {
  mode: 'student' | 'teacher'
  /** Where this side's own things live: slots, calendar link, notifications. */
  base: '/me' | '/teach'
  home: '/' | '/teach'
  /** The teacher key being viewed as, in this tab. */
  as: string | null
  /** The data hook of this side. */
  useHome: () => HomeData
}

// Called when a screen renders, never while this module loads: the hooks are consts built by a factory in modules
// that load around this one, and reading them early would throw for every user.
function useStudentHome() {
  return useTimetable()
}
function useTeacherHome() {
  return useTeachTimetable()
}

const STUDENT: Viewer = { mode: 'student', base: '/me', home: '/', as: null, useHome: useStudentHome }

const Ctx = createContext<Viewer>(STUDENT)

/** The student viewer unless inside TeacherScope. */
export const useViewer = () => useContext(Ctx)
export const useHome = () => useViewer().useHome()

/**
 * Who may use the teacher side: a teacher, a student with teaching rights (a teaching assistant), or an owner who
 * chose a teacher to view as in this tab.
 */
export const mayTeach = (s: SessionInfo) => s.kind === 'teacher' || (s.kind === 'student' && !!s.teaching) || (s.role === 'owner' && !!viewAs.get())

/** The teacher side's pages: everything inside reads the teacher's data. Anyone else goes to /teach, which sorts them out. */
export function TeacherScope({ children }: { children: ReactNode }) {
  const { session } = useSession()
  const as = session?.role === 'owner' ? viewAs.get() : null
  const viewer = useMemo<Viewer>(() => ({ mode: 'teacher', base: '/teach', home: '/teach', as, useHome: useTeacherHome }), [as])
  if (!session) return <div className="min-h-dvh bg-cream" aria-busy="true" />
  if (!mayTeach(session)) return <Navigate to="/teach" replace />
  return <Ctx.Provider value={viewer}>{children}</Ctx.Provider>
}
