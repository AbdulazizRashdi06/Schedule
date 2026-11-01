import { useEffect, useState } from 'react'
import { API_BASE, api, errorText } from '../lib/api'
import { Checkbox, ErrorText } from '../components/ui'
import { shortDate } from './AdminLayout'

interface State {
  enabled: boolean
  updatedBy: string | null
  updatedAt: string | null
}

/** Owners switch the public timetable API on or off: the published semester's classes, for anyone, as JSON or CSV. */
export default function PublicTimetable() {
  const [state, setState] = useState<State | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState('')
  useEffect(() => {
    api.get<State>('/admin/public-timetable').then(setState, (e) => setError(errorText(e)))
  }, [])

  const base = (API_BASE || location.origin) + '/api/public/timetable'
  const links = [
    ['JSON', base],
    ['CSV', base + '.csv'],
  ] as const

  return (
    <section className="flex max-w-[760px] flex-col gap-3 rounded-[20px] p-5 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
      <div className="flex flex-col gap-1">
        <h2 className="text-[24px] font-extrabold tracking-[-0.02em]">Public timetable API</h2>
        <p className="text-[15px] font-semibold text-muted">
          While it’s on, anyone can read the published semester’s classes (course, section, teacher, date, time and room), with no sign-in. Nothing about students is in it.
        </p>
      </div>
      {state && (
        <Checkbox
          checked={state.enabled}
          label={state.enabled ? 'On: the links below work for anyone' : 'Off: the links answer “switched off”'}
          onChange={async (enabled) => {
            if (busy) return
            setBusy(true)
            setError('')
            try {
              setState(await api.put<State>('/admin/public-timetable', { enabled }))
            } catch (e) {
              setError(errorText(e))
            } finally {
              setBusy(false)
            }
          }}
        />
      )}
      <ul className="flex flex-col gap-2">
        {links.map(([label, url]) => (
          <li key={label} className="flex flex-wrap items-center gap-2.5 rounded-[14px] bg-paper px-3.5 py-2.5">
            <span className="label text-[11px]">{label}</span>
            <code className="min-w-0 flex-1 font-mono text-[13px] font-bold break-all">{url}</code>
            <button
              type="button"
              className="label text-[11px] link-hover"
              onClick={async () => {
                await navigator.clipboard.writeText(url).catch(() => {})
                setCopied(label)
              }}
            >
              {copied === label ? 'Copied' : 'Copy'}
            </button>
          </li>
        ))}
      </ul>
      {state?.updatedAt && (
        <p className="text-[13px] font-semibold text-muted">
          Last changed {shortDate(state.updatedAt)} by {state.updatedBy}.
        </p>
      )}
      <ErrorText>{error}</ErrorText>
    </section>
  )
}
