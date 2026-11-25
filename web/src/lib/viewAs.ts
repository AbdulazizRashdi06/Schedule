/**
 * "View as": the teacher an owner is looking at the teacher side as, for this browser tab only (sessionStorage, so it
 * is never left behind for the next person and never saved offline). No imports on purpose: lib/api reads it, and
 * anything it pulled in would be loaded before the rest of the app.
 */
const KEY = 'teach-as'
const NAME = 'teach-as-name'

const read = (key: string): string | null => {
  try {
    return sessionStorage.getItem(key)
  } catch {
    return null
  }
}

export const viewAs = {
  /** The teacher key being viewed as ("t-3" or "n-…"), or null. */
  get: (): string | null => read(KEY) || null,
  /** That teacher's name, for the bar that says who is being viewed. */
  name: (): string | null => read(NAME) || null,
  set(key: string, name: string) {
    try {
      sessionStorage.setItem(KEY, key)
      sessionStorage.setItem(NAME, name)
    } catch {
      /* no storage: "view as" just doesn't start */
    }
  },
  clear() {
    try {
      sessionStorage.removeItem(KEY)
      sessionStorage.removeItem(NAME)
    } catch {
      /* ignore */
    }
  },
}
