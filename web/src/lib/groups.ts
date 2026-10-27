import { store } from './api'

export type GroupKind = 'friends' | 'car'

export interface GroupSummary {
  id: number
  name: string
  kind: GroupKind
  members: number
}

export interface GroupMemberInfo {
  id: number
  name: string
  me: boolean
  showClasses: boolean
}

export interface Group {
  id: number
  name: string
  kind: GroupKind
  inviteCode: string
  travelMinutes: number
  arriveEarlyMinutes: number
  members: GroupMemberInfo[]
}

export interface Window {
  start: string
  end: string
  minutes: number
}

export interface GroupDay {
  date: string
  day: number
  free: Window[]
  nearly: (Window & { busy: string[] })[]
  classes: { member: string; start: string; end: string; label: string; room: string }[]
}

export interface GroupWeek {
  start: string
  members: number
  days: GroupDay[]
}

export const KIND_NAME: Record<GroupKind, string> = { friends: 'Friends', car: 'Car' }

export const inviteLink = (code: string) => `${location.origin}/join/${code}`

const PENDING = 'pending-join'

/** An invite opened while signed out waits here until sign-in. */
export const rememberInvite = (code: string) => store.set(PENDING, code)

/** Where to go after sign-in: a waiting invite, or Today. */
export function afterSignIn() {
  const code = store.get(PENDING)
  if (!code) return '/'
  store.remove(PENDING)
  return `/join/${code}`
}
