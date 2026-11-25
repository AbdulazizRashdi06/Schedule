import { api } from '../lib/api'
import { createHomeStore } from '../lib/homeStore'
import type { TeachMe, Timetable } from '../lib/types'
import { viewAs } from '../lib/viewAs'
import { buildTeachSessions } from './teachSessions'

// The teacher side's timetable: a second instance of the home store, sharing nothing with the student's
// (student/timetable), so a teaching assistant who uses both homes never sees one in the other.
const home = createHomeStore({
  savedKey: 'timetable:teach',
  fetch: async () => {
    const teach = await api.get<TeachMe>('/teach/me')
    const tt = await api.get<Timetable>('/teach/timetable')
    return { me: null, teach, tt }
  },
  seenUrl: '/teach/seen',
  build: buildTeachSessions,
  // An owner viewing as a teacher keeps nothing on the device.
  canSave: () => !viewAs.get(),
})

/** Forget the cached teaching timetable (after the teacher edits their classes or picks a name). */
export const clearTeachCache = home.clear

/** On sign-out: forget everything about this teacher, including a load still on its way. */
export const resetTeach = home.reset

/** Who the data in memory was loaded as: when "view as" starts, changes or ends, it is somebody else's. */
let loadedAs = viewAs.get()

export function useTeachTimetable() {
  const as = viewAs.get()
  if (as !== loadedAs) {
    loadedAs = as
    home.reset()
  }
  return home.useData()
}
