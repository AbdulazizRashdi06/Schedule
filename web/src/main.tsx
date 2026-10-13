import { StrictMode, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import './index.css'
import { SessionProvider, useSession } from './lib/session'
import { Loading } from './components/ui'
import Login from './student/Login'
import Home from './student/Home'
import Setup from './student/Setup'
import EditCourses from './student/EditCourses'
import AdminLogin from './admin/AdminLogin'
import ChangePassword from './admin/ChangePassword'
import AdminLayout from './admin/AdminLayout'
import AdminHome from './admin/AdminHome'
import Upload from './admin/Upload'
import Courses from './admin/Courses'
import CourseDetail from './admin/CourseDetail'
import Majors from './admin/Majors'
import Students from './admin/Students'
import StudentDetail from './admin/StudentDetail'
import Accounts from './admin/Accounts'

function StudentOnly({ children }: { children: ReactNode }) {
  const { session } = useSession()
  if (!session) return <Loading />
  if (session.kind === 'admin') return <Navigate to="/admin" replace />
  if (session.kind !== 'student') return <Navigate to="/login" replace />
  return children
}

function StaffOnly({ children, ownerOnly }: { children: ReactNode; ownerOnly?: boolean }) {
  const { session } = useSession()
  const location = useLocation()
  if (!session) return <Loading />
  if (session.kind === 'admin' && session.mustChangePassword) return <Navigate to="/admin/password" replace />
  const staff = session.kind === 'admin' || session.role === 'owner'
  if (!staff) return <Navigate to={session.kind === 'student' ? '/' : '/admin/login'} replace state={{ from: location.pathname }} />
  if (ownerOnly && session.role !== 'owner') return <Navigate to="/admin" replace />
  return children
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SessionProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/" element={<StudentOnly><Home /></StudentOnly>} />
          <Route path="/setup" element={<StudentOnly><Setup /></StudentOnly>} />
          <Route path="/edit" element={<StudentOnly><EditCourses /></StudentOnly>} />
          <Route path="/admin/login" element={<AdminLogin />} />
          <Route path="/admin/password" element={<ChangePassword />} />
          <Route path="/admin" element={<StaffOnly><AdminLayout /></StaffOnly>}>
            <Route index element={<AdminHome />} />
            <Route path="upload" element={<Upload />} />
            <Route path="courses" element={<Courses />} />
            <Route path="courses/:id" element={<CourseDetail />} />
            <Route path="majors" element={<Majors />} />
            <Route path="students" element={<StaffOnly ownerOnly><Students /></StaffOnly>} />
            <Route path="students/:id" element={<StaffOnly ownerOnly><StudentDetail /></StaffOnly>} />
            <Route path="accounts" element={<Accounts />} />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </SessionProvider>
  </StrictMode>,
)
