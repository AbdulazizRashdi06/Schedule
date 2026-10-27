import { useState } from 'react'
import { api, errorText } from '../lib/api'
import { ErrorText } from '../components/ui'

interface Imported {
  imported: number
  notFound: string[]
  noSection: string[]
}

/** "Get my courses from GUtech": replaces this semester's courses with the ones GUtech lists for the student. */
export default function ImportCourses({ onDone }: { onDone: () => void | Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<Imported | null>(null)
  const [error, setError] = useState('')

  return (
    <div className="flex flex-col gap-2 rounded-[18px] bg-ice p-4">
      <p className="text-[15px] font-bold">GUtech can fill in your courses and sections for you.</p>
      <button
        type="button"
        disabled={busy}
        onClick={async () => {
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
        }}
        className="press h-11 self-start rounded-full bg-navy px-4 text-[15px] font-extrabold text-cream"
      >
        {busy ? 'Getting them…' : 'Get my courses from GUtech'}
      </button>
      {result && (
        <p role="status" className="text-[14px] font-semibold">
          Added {result.imported} {result.imported === 1 ? 'course' : 'courses'} from GUtech.
          {result.noSection.length > 0 && ` Pick a section for ${result.noSection.join(', ')}.`}
          {result.notFound.length > 0 && ` Not in Classy yet: ${result.notFound.join(', ')}.`}
        </p>
      )}
      <ErrorText>{error}</ErrorText>
    </div>
  )
}
