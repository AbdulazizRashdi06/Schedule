export type CourseType = 'Core' | 'LS' | 'Elective'

export interface Meeting {
  day: number
  start: string
  end: string
  room: string
}

export interface Section {
  id: number
  number: string
  instructor: string
  meetings: Meeting[]
  changedAt: string | null
  changeSummary: string | null
}

export interface Course {
  id: number
  code: string
  name: string
  type: CourseType
  year: number | null
  credits: number | null
  majors: string[]
  sections: Section[]
}

export interface SessionInfo {
  kind: 'student' | 'admin' | null
  name?: string
  role?: 'owner' | 'rsa' | null
  mustChangePassword?: boolean
}

export interface Me {
  universityId: string
  email: string
  isOwner: boolean
  major: { id: number; code: string; name: string } | null
  year: number | null
  predictedYear: number
  semester: { id: number; name: string } | null
  step: Step
  setupDone: boolean
}

export type Step = 'major' | 'year' | 'core' | 'ls' | 'electives' | 'done' | 'waiting'

export interface Major {
  id: number
  code: string
  name: string
}

export interface Options {
  semester: { id: number; name: string }
  year: number | null
  courses: Course[]
  previous: string[]
}

export interface PickRow {
  courseId: number
  sectionId: number | null
  sectionRemoved: boolean
}

export interface TimetableClass {
  courseId: number
  code: string
  name: string
  type: CourseType
  credits: number | null
  section: Section
  changed: boolean
}

export interface Timetable {
  semester: { id: number; name: string } | null
  classes: TimetableClass[]
  needsSection: { courseId: number; code: string; name: string; removed: boolean }[]
  clashes: [number, number][]
}

export interface Semester {
  id: number
  name: string
  term: string
  year: number
  isCurrent: boolean
  publishedAt: string | null
  courses?: number
  sections?: number
}
