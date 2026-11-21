import { useState } from 'react'
import { api, errorText } from '../lib/api'
import { Confirm, ErrorText } from '../components/ui'

interface Imported {
  imported: number
  notFound: string[]
  noSection: string[]
  /** Courses the student had that GUtech doesn't list, now gone. */
  removed: string[]
}

/**
 * "Get my courses from GUtech": replaces this semester's courses with the ones GUtech lists for the student.
 * With courses already there (current), it asks first. With unsaved edits on the page it waits, so they aren't thrown away.
 */
export default function ImportCourses({ current = 0, unsaved = false, onDone }: { current?: number; unsaved?: boolean; onDone: () => void | Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [asking, setAsking] = useState(false)
  const [result, setResult] = useState<Imported | null>(null)
  const [error, setError] = useState('')

  const run = async () => {
    setBusy(true)
    setError('')
    try {
      const r = await api.post<Imported>('/me/import-courses')
      setResult(r)
      await onDone()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-2 rounded-[18px] bg-ice p-4">
      <p className="text-[15px] font-bold">
        {current > 0 ? 'GUtech can set your courses and sections to match its list. Courses it doesn’t list are removed.' : 'GUtech can fill in your courses and sections for you.'}
      </p>
      <button
        type="button"
        disabled={busy || unsaved}
        onClick={() => (current > 0 ? setAsking(true) : void run())}
        className="press h-11 self-start rounded-full bg-navy px-4 text-[15px] font-extrabold text-cream disabled:opacity-50"
      >
        {busy ? 'Getting them…' : 'Get my courses from GUtech'}
      </button>
      {unsaved && <p className="text-[14px] font-semibold">Save or undo your changes first.</p>}
      {result && (
        <p role="status" className="text-[14px] font-semibold">
          Your courses now match GUtech: {result.imported} {result.imported === 1 ? 'course' : 'courses'}.
          {result.removed.length > 0 && ` Removed: ${result.removed.join(', ')}.`}
          {result.noSection.length > 0 && ` Pick a section for ${result.noSection.join(', ')}.`}
          {result.notFound.length > 0 && ` Not in Classy yet: ${result.notFound.join(', ')}.`}
        </p>
      )}
      <ErrorText>{error}</ErrorText>
      <Confirm
        open={asking}
        onClose={() => setAsking(false)}
        title="Replace your courses?"
        body="Your courses this semester become the ones GUtech lists. Any course GUtech doesn’t list is removed from your week; you can add it back yourself."
        confirmLabel="Replace them"
        onConfirm={run}
      />
    </div>
  )
}
