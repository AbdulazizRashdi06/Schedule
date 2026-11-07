import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, TextField } from '../components/ui'

/**
 * The university's attendance limit, as a percentage of classes a student may miss. Students' own absence counts show
 * it and warn as they get close. Owners change it; unset, students just see their count.
 */
export default function AttendanceLimit() {
  const [percent, setPercent] = useState('')
  const [saved, setSaved] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    api.get<{ percent: number | null }>('/admin/attendance-limit').then(
      (r) => (setSaved(r.percent), setPercent(r.percent?.toString() ?? '')),
      (e) => setError(errorText(e)),
    )
  }, [])

  const save = async (value: number | null) => {
    setBusy(true)
    setError('')
    setStatus('')
    try {
      const r = await api.put<{ percent: number | null }>('/admin/attendance-limit', { percent: value })
      setSaved(r.percent)
      setPercent(r.percent?.toString() ?? '')
      setStatus('Saved.')
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.12)]">
      <div className="flex flex-col gap-1">
        <h3 className="text-[18px] font-extrabold">Attendance limit</h3>
        <span className="text-[14px] font-semibold text-muted">
          The share of classes a student may miss. Students count their own absences in Classy and get a warning near it.
        </span>
      </div>
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          const n = Number(percent)
          if (!Number.isInteger(n) || n < 1 || n > 100) return setError('Enter a whole percentage, like 25.')
          void save(n)
        }}
      >
        <TextField label="Limit (%)" inputMode="numeric" placeholder="25" value={percent} onChange={(e) => setPercent(e.target.value)} className="w-[120px]" mono />
        <Button type="submit" busy={busy} className="h-12 px-5 text-[15px]">
          Save
        </Button>
        {saved !== null && (
          <Button type="button" variant="ghost" className="h-12 px-4 text-[14px]" onClick={() => void save(null)}>
            No limit
          </Button>
        )}
      </form>
      {status && <span className="text-[14px] font-bold">{status}</span>}
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
