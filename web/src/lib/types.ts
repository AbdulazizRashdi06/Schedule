export type CourseType = 'Core' | 'LS' | 'Elective'

export interface Meeting {
  day: number
  start: string
  end: string
  room: string
  /** "2026-09-21": a class on that date only (timetables that change week to week). Null = every week on `day`. */
  date: string | null
  excludedDates?: string[] | null
  /** Set on a session a teacher's change put on (the class at its new time or room, an extra class): that change's id. */
  changeId?: number
  /** Sent for a session a change put on, so a lab moved to a typed room stays a lab. Otherwise it comes from the room. */
  type?: 'lecture' | 'lab' | 'tutorial'
  /** Teacher side: sessions that share a key are one real class listed under several course codes. */
  merge?: string
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
  kind: 'student' | 'admin' | 'teacher' | null
  name?: string
  role?: 'owner' | 'rsa' | 'batch-rep' | 'teacher' | null
  batchRep?: { homeFamily: string } | null
  mustChangePassword?: boolean
  /** A student with teaching rights (a teaching assistant): they have a way into the teacher side. */
  teaching?: boolean
  /** The last emailed code entered in this browser is recent enough to change a class. */
  codeFresh?: boolean
  /** For a teacher's own sign-in. */
  teacher?: { id: number; displayName: string }
}

export interface Me {
  universityId: string
  email: string
  isOwner: boolean
  batchRep?: { homeFamily: string } | null
  plannerEnabled?: boolean
  major: { id: number; code: string; name: string } | null
  year: number | null
  predictedYear: number
  semester: { id: number; name: string; weeks?: Weeks } | null
  step: Step
  setupDone: boolean
  /** GUtech can fill in this student's courses ("Get my courses from GUtech"). */
  canImport?: boolean
}

/** Teaching weeks: week 1's Sunday ("2026-09-20") and the last week's number. `set` = chosen by an admin. */
export interface Weeks {
  week1Start: string | null
  lastWeek: number | null
  set: boolean
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
  /** Notes I can see: on the whole class, and per session date. */
  notes?: { classWide: number; dates: Record<string, number> }
  /** Teacher side: how many Classy users picked the section. Not the official enrollment. */
  students?: number
  /** Teacher side: listed under my name / one I added myself. */
  own?: boolean
  added?: boolean
  /** Who teaches it, with their office hours and details. */
  teachers?: TeacherRef[]
}

/** A teacher of a class: the name on the university timetable, or a teacher who added the class. */
export interface TeacherRef {
  name: string
  /** "n-…" or "t-…", for /teacher/{key}. */
  key: string
  /** false for placeholder names: plain text, no page. */
  tappable: boolean
  /** true = named on the university timetable; false = a teacher who added this class. */
  listed: boolean
  officeRoom?: string
  note?: string
  email?: string | null
  officeHours?: { day: number; start: string; end: string; room: string }[]
}

/** One change a teacher made to one session of one section. */
export interface ClassChange {
  id: number
  group: string
  kind: 'cancel' | 'move' | 'room' | 'extra'
  courseId: number
  sectionId: number
  code: string
  /** Sent, not guessed from the room: a lab moved to a typed room stays a lab. */
  type: 'lecture' | 'lab' | 'tutorial'
  /** The session as the university lists it; null for an extra class. */
  from: { date: string; start: string; end: string; room: string } | null
  /** Where it is now; null for a cancelled class. room '' = to be announced. */
  to: { date: string; start: string; end: string; room: string } | null
  note: string | null
  /** "Raed Bolbol", or "Classy staff". */
  by: string
  at: string
  // Teacher side only:
  canUndo?: boolean
  canEdit?: boolean
  mine?: boolean
  flagged?: boolean
}

/** A teacher's weekly office hour, as it shows on their own timetable. */
export interface OfficeHour {
  id: number
  day: number
  start: string
  end: string
  room: string
  /** The days off on that weekday. */
  excludedDates?: string[] | null
}

export interface TeachNotice {
  id: number
  kind: string
  title: string
  body: string
  url: string
  at: string
}

/** Who is using the teacher side (GET /teach/me). */
export interface TeachMe {
  /** null: an owner viewing a name nobody has claimed. */
  id: number | null
  email: string | null
  /** The name students see. */
  name: string
  /** A staff account, a student with teaching rights, or an owner viewing as a teacher. */
  kind: 'staff' | 'student' | 'view'
  /** The name being viewed, in "view as". */
  as: string | null
  /** false in "view as": own slots, devices, the calendar link and the name step belong to the teacher. */
  personal: boolean
  semester: { id: number; name: string; weeks: Weeks } | null
  plannerEnabled: boolean
  status: 'linked' | 'pending' | 'pick-name' | 'none'
  names: { name: string; state: 'auto' | 'approved' | 'pending' | 'rejected' }[]
  frozen: boolean
  changesEnabled: boolean
  codeFresh: boolean
  /** Unseen, from the last 14 days. */
  notices: TeachNotice[]
}

/** The body of a change's preview, create and replace. */
export interface ChangeBody {
  kind: 'cancel' | 'move' | 'room' | 'extra'
  sectionId?: number
  date?: string
  start?: string
  sectionIds?: number[]
  alsoOther?: boolean
  newDate?: string
  newStart?: string
  newEnd?: string
  room?: string | null
  type?: 'lecture' | 'lab' | 'tutorial' | null
  note?: string
  groupId?: string | null
}

export interface ChangePreview {
  codes: { sectionId: number; code: string; number: string; students: number; named: string; yours: boolean; changedBy: string | null }[]
  size: number
  others: { start: string; end: string; room: string }[]
  needsCode: boolean
  blocked: 'started' | 'switched-off' | null
  warnings: { code: string; text: string }[]
}

/** What students see of a teacher (GET /teachers/{key}). */
export interface TeacherPage {
  name: string
  hasAccount: boolean
  officeRoom: string
  note: string
  email: string | null
  officeHours: { day: number; start: string; end: string; room: string }[]
  week: { from: string; to: string }
  classes: { codes: string[]; name: string; section: string; date: string; start: string; end: string; room: string; type: string; status: null | 'cancelled' | 'moved' | 'room' | 'extra'; note: string | null }[]
}

/** A student's own slot: every week on `day`, or once on `date`. */
export interface CustomSlot {
  id: number
  title: string
  day: number
  start: string
  end: string
  date: string | null
  place: string
  note: string | null
}

export interface Timetable {
  plannerEvents?: StudentPlannerEvent[]
  /** Days with no classes this semester (holidays, breaks): weekly classes already skip them. */
  daysOff?: { date: string; name: string }[]
  semester: { id: number; name: string } | null
  classes: TimetableClass[]
  needsSection: { courseId: number; code: string; name: string; removed: boolean }[]
  clashes: [number, number][]
  slots?: CustomSlot[]
  /** The semester's first and last teaching day: weekly classes and slots only meet between them. Missing when unknown. */
  span?: { first: string; last: string } | null
  /** Teachers' changes to these classes, for the marks. The meetings above already have them applied. */
  changes?: ClassChange[]
  /** Teacher side: my office hours, the Classy users of each merged class, and classes I added that the university removed. */
  officeHours?: OfficeHour[]
  mergeSizes?: Record<string, number>
  removed?: { code: string; number: string }[]
}

export interface StudentPlannerEvent {
  id: number
  version: number
  kind: 'exam' | 'class'
  title: string
  date: string
  start: string
  end: string
  room: string
  notes: string | null
  courseId: number
  code: string
  courseName: string
  sectionId: number | null
  updatedAt: string
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
