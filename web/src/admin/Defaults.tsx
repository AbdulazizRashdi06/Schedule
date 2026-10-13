import { useEffect, useState } from 'react'
import { ApiError, api, errorText } from '../lib/api'
import { Button, ErrorText, Loading, Notice } from '../components/ui'
import { PageTitle, useAdmin } from './AdminLayout'

interface DefaultRow {
  id: string
  major: string
  year: number
}

const EXAMPLE = `[
  { "id": "CS 1 101", "major": "COMP", "year": 1 },
  { "id": "AI 2 102", "major": "AI", "year": 2 }
]`

/** Which courses are ticked for a major or family and year: the whole list as JSON, replaced on save. */
export default function Defaults() {
  const { semester } = useAdmin()
  const [text, setText] = useState<string | null>(null)
  const [saved, setSaved] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [problems, setProblems] = useState<string[]>([])

  useEffect(() => {
    if (!semester) return
    setText(null)
    setSaved('')
    api.get<DefaultRow[]>(`/admin/semesters/${semester.id}/defaults`).then(
      (rows) => setText(rows.length ? JSON.stringify(rows, null, 2).replace(/\{\n\s+/g, '{ ').replace(/,\n\s+"/g, ', "').replace(/\n\s+\}/g, ' }') : ''),
      (e) => setError(errorText(e)),
    )
  }, [semester])

  const save = async () => {
    if (!semester || text === null) return
    setError('')
    setProblems([])
    setSaved('')
    let rows: unknown
    try {
      rows = text.trim() ? JSON.parse(text) : []
    } catch {
      return setError('That isn’t valid JSON. Check the brackets and commas.')
    }
    if (!Array.isArray(rows)) return setError('Start with [ and end with ]: a list of { "id", "major", "year" }.')
    setBusy(true)
    try {
      const r = await api.put<{ saved: number }>(`/admin/semesters/${semester.id}/defaults`, rows)
      setSaved(`Saved ${r.saved} ${r.saved === 1 ? 'default' : 'defaults'} for ${semester.name}.`)
    } catch (e) {
      setError(errorText(e))
      if (e instanceof ApiError) setProblems(((e.data as { problems?: string[] } | undefined)?.problems ?? []).slice(0, 50))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageTitle title="Defaults" sub={semester ? `Courses ticked for each major or family and year in ${semester.name}.` : undefined} />
      {!semester ? (
        <Notice>Create a semester under Upload first.</Notice>
      ) : (
        <section className="flex max-w-[760px] flex-col gap-4">
          <div className="flex flex-col gap-1.5 text-[15px] font-semibold text-muted">
            <span>
              Each row is a course code (<span className="font-mono font-bold text-navy">id</span>), a major or family code (<span className="font-mono font-bold text-navy">major</span>) and a year (
              <span className="font-mono font-bold text-navy">year</span>, 0 is Foundation).
            </span>
            <span>A major’s own rows for a year replace its family’s. With no rows for a year, the upload’s core courses are ticked.</span>
            <span>Saving replaces the whole list for this semester.</span>
          </div>
          {text === null ? (
            !error && <Loading rows={4} />
          ) : (
            <>
              <div className="flex flex-col gap-2">
                <label htmlFor="defaults" className="label text-[11px] tracking-[0.12em]">
                  Defaults (JSON)
                </label>
                <textarea
                  id="defaults"
                  spellCheck={false}
                  placeholder={EXAMPLE}
                  className="field h-[360px] rounded-[20px] bg-paper p-4 font-mono text-[13px] outline-none placeholder:text-[#9AA3C7]"
                  value={text}
                  onChange={(e) => {
                    setText(e.target.value)
                    setSaved('')
                  }}
                />
              </div>
              <div className="flex flex-wrap gap-2">
                <Button busy={busy} onClick={save}>
                  {busy ? 'Saving…' : 'Save'}
                </Button>
                <label className="press field inline-flex h-12 cursor-pointer items-center rounded-full px-5 text-[15px] font-extrabold">
                  Open a .json file
                  <input
                    type="file"
                    accept=".json,application/json"
                    className="sr"
                    onChange={async (e) => {
                      const f = e.target.files?.[0]
                      if (f) setText(await f.text())
                      e.target.value = ''
                    }}
                  />
                </label>
              </div>
            </>
          )}
          {saved && <Notice>{saved}</Notice>}
          <ErrorText>{error}</ErrorText>
          {problems.length > 0 && (
            <ul className="flex flex-col gap-1 text-[14px] font-semibold text-alert">
              {problems.map((p) => (
                <li key={p}>{p}</li>
              ))}
            </ul>
          )}
        </section>
      )}
    </>
  )
}
