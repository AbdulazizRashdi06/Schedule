import { createContext } from 'react'
import type { Session } from '../lib/slots'
import type { CustomSlot } from '../lib/types'

/**
 * Set by the home page, where the days and the week are one page: links that would open another screen move along the
 * page instead. Screens without it (the course page, the old schedule) keep their plain links.
 */
export const LabNav = createContext<{
  /** Scroll down to the week. */
  week: () => void
  /** Scroll to a day's page. */
  day: (date: Date) => void
  menu: () => void
  /** "+ Add a slot" on a day. */
  add: (date: Date) => void
  /** Open one of your own slots to edit. */
  edit: (slot: CustomSlot) => void
  /** Where a class on a date opens: its session page (today when no date is given). */
  session: (s: Session, date?: Date) => string
} | null>(null)
