import type { Course, Semester, Weeks } from '../lib/types'

export interface Target { courseId: number; sectionIds: number[] }
export interface Source { courseId: number; sectionId: number; fingerprint: string; date: string; start: string; end: string }
export interface Spec { kind: 'exam' | 'class'; action: 'add' | 'replace' | 'cancel'; title: string; date: string; start: string; end: string; room: string; targets: Target[]; repeatUntil: string | null; source: Source | null; notes: string | null }
export interface Entry { key: string; eventId: number | null; proposalId: number | null; version: number; kind: 'exam' | 'class'; title: string; date: string; start: string; end: string; room: string; targets: Target[]; status: string; source: Source | null; stale: boolean; notes: string | null }
export interface Proposal { id: number; eventId: number | null; baseEventVersion: number; version: number; status: string; updatedBy: string; spec: Spec }
export interface Batch { id: number; name: string; majorId: number | null; year: number | null; version: number; targets: Target[] }
export interface Workspace { id: number; semesterId: number; family: string; dailyExamWarning: number; weeklyExamWarning: number; version: number }
export interface Activity { id: number; action: string; actor: string; explanation: string; at: string; eventId: number | null; snapshotJson: string }
export interface Board { workspace: Workspace; batches: Batch[]; proposals: Proposal[]; history: Activity[] }
export interface Catalog { enabled: boolean; isOwner: boolean; assignment: { homeFamily: string } | null; semesters: Semester[]; semesterId: number | null; majors: { id: number; code: string; name: string; family: string }[]; courses: Course[]; weeks: Weeks | null }
export interface Analysis { token: string; conflicts: { kind: string; date: string; message: string; courses: string[]; sectionIds: number[] }[]; coverageWarnings: string[] }
export interface SlotRequest { targets: Target[]; kind: 'exam' | 'class'; from: string; to: string; duration: number; weekdays: number[]; start: string; end: string; room: string; repeatUntil: string | null }
export interface Candidate { spec: Spec; score: number; analysis: Analysis; explanation: string }
export interface AiResult { intent: 'proposal' | 'find-slots' | 'clarify'; kind: 'exam' | 'class'; action: Spec['action']; title: string; courseIds: number[]; sectionIds: number[]; batchIds: number[]; date: string | null; to: string | null; start: string | null; end: string | null; duration: number | null; room: string; repeatUntil: string | null; sourceKey: string | null; missingFields: string[]; explanation: string }
export interface AiJob { id: string; status: string; error: string | null; result: AiResult }

export const emptySpec = (date: string, targets: Target[] = []): Spec => ({ kind: 'exam', action: 'add', title: '', date, start: '09:00', end: '11:00', room: '', targets, repeatUntil: null, source: null, notes: '' })
export const dateValue = (s: string) => new Date(`${s}T12:00:00`)
export const campusToday = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Muscat', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date())
export const courseColour = (courseId: number) => ['bg-sky text-navy', 'bg-ice text-navy', 'bg-navy text-cream', 'bg-paper text-navy'][Math.abs(courseId) % 4]
