import { useEffect, useMemo, useState } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, CourseType, Me, Options, PickRow } from '../lib/types'
import { BackIcon, Button, ErrorText, Input, Loading, Notice, Sheet } from '../components/ui'
import { CourseRow, type Picks } from '../components/CourseRow'
import { sectionSummary } from '../lib/time'
import { StudentShell } from './StudentShell'

const GROUPS: { type: CourseType; label: string }[] = [
  { type: 'Core', label: 'Core' },
  { type: 'LS', label: 'Life Skills' },
  { type: 'Elective', label: 'Electives' },
]

/** Part D: every pick on one page, saved once. */
export default function EditCourses() {
  const navigate = useNavigate()
  const [me, setMe] = useState<Me | null>(null)
  const [options, setOptions] = useState<Options | null>(null)
  const [picks, setPicks] = useState<Picks>(new Map())
  const [saved, setSaved] = useState<Picks>(new Map())
  const [adding, setAdding] = useState<CourseType | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    api.get<Me>('/me').then(async (m) => {
      setMe(m)
      if (!m.setupDone) return
      const [o, p] = await Promise.all([api.get<Options>('/me/options'), api.get<PickRow[]>('/me/picks')])
      setOptions(o)
      const map: Picks = new Map(p.map((x) => [x.courseId, x.sectionId]))
      setPicks(map)
      setSaved(map)
    }).catch((e) => setError(errorText(e)))
  }, [])

  const byId = useMemo(() => new Map((options?.courses ?? []).map((c) => [c.id, c])), [options])
  if (me && !me.setupDone) return <Navigate to="/setup" replace />
  if (error && !options) return <StudentShell setupDone><ErrorText>{error}</ErrorText></StudentShell>
  if (!me || !options) return <StudentShell setupDone><Loading /></StudentShell>

  const dirty = picks.size !== saved.size || [...picks].some(([k, v]) => saved.get(k) !== v || !saved.has(k))
  const missing = [...picks].filter(([cid, sid]) => sid == null && (byId.get(cid)?.sections.length ?? 0) > 0).map(([cid]) => byId.get(cid)!.code)

  return (
    <StudentShell
      setupDone
      title={
        <div className="flex items-center gap-1">
          <button onClick={() => navigate('/')} className="-ml-2 rounded-lg p-2 text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800" aria-label="Back to timetable">
            <BackIcon />
          </button>
          <div>
            <div className="text-lg font-semibold leading-tight">Edit my courses</div>
            <div className="text-xs text-zinc-500">{options.semester.name}</div>
          </div>
        </div>
      }
    >
      <div className="space-y-6">
        {GROUPS.map((g) => {
          const list = options.courses.filter((c) => c.type === g.type && picks.has(c.id))
          return (
            <section key={g.type} className="space-y-2.5">
              <div className="flex items-center justify-between">
                <h2 className="text-sm font-semibold text-zinc-500">{g.label}</h2>
                <button onClick={() => setAdding(g.type)} className="text-sm font-medium text-indigo-600 dark:text-indigo-400">
                  + Add
                </button>
              </div>
              {list.length === 0 && <p className="text-sm text-zinc-400">None</p>}
              {list.map((c) => (
                <CourseRow key={c.id} course={c} picks={picks} byId={byId} onChange={setPicks} mode="remove" />
              ))}
            </section>
          )
        })}
      </div>

      <div className="sticky bottom-0 -mx-5 mt-6 border-t border-zinc-200/70 bg-zinc-50/90 px-5 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] backdrop-blur dark:border-zinc-800 dark:bg-zinc-950/90">
        <ErrorText>{error}</ErrorText>
        {missing.length > 0 && <p className="mb-2 text-center text-xs text-amber-700 dark:text-amber-400">Pick a section for {missing.join(', ')}</p>}
        <Button
          className="w-full"
          disabled={!dirty || missing.length > 0 || picks.size === 0}
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await api.put('/me/picks', { picks: [...picks].map(([courseId, sectionId]) => ({ courseId, sectionId })) })
              navigate('/')
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          {dirty ? 'Save changes' : 'No changes'}
        </Button>
      </div>

      <AddSheet
        type={adding}
        me={me}
        courses={options.courses.filter((c) => c.type === adding && !picks.has(c.id))}
        onClose={() => setAdding(null)}
        onAdd={(c: Course) => {
          const next = new Map(picks)
          next.set(c.id, c.sections.length === 1 ? c.sections[0].id : null)
          setPicks(next)
          setAdding(null)
        }}
      />
    </StudentShell>
  )
}

function AddSheet({ type, me, courses, onClose, onAdd }: { type: CourseType | null; me: Me; courses: Course[]; onClose: () => void; onAdd: (c: Course) => void }) {
  const [q, setQ] = useState('')
  const term = q.trim().toLowerCase()
  const list = courses
    .filter((c) => !term || c.code.toLowerCase().includes(term.replace(/\s/g, '')) || c.name.toLowerCase().includes(term))
    .sort((a, b) => (type === 'Core' ? Math.abs((a.year ?? 9) - (me.year ?? 0)) - Math.abs((b.year ?? 9) - (me.year ?? 0)) : 0) || a.code.localeCompare(b.code))
  const label = type === 'Core' ? 'core course' : type === 'LS' ? 'Life Skills course' : 'elective'
  return (
    <Sheet open={type != null} onClose={onClose} title={`Add a ${label}`}>
      <div className="space-y-3">
        <Input placeholder="Search by code or name" value={q} onChange={(e) => setQ(e.target.value)} />
        {list.length === 0 && <Notice>Nothing else to add.</Notice>}
        <div className="-mx-2 max-h-[55vh] space-y-1 overflow-y-auto">
          {list.map((c) => (
            <button key={c.id} onClick={() => onAdd(c)} className="block w-full rounded-xl px-3 py-2.5 text-left hover:bg-zinc-100 dark:hover:bg-zinc-800">
              <div className="flex items-baseline gap-2">
                <span className="font-semibold">{c.code}</span>
                <span className="truncate text-zinc-600 dark:text-zinc-400">{c.name}</span>
                {type === 'Core' && c.year && <span className="ml-auto shrink-0 text-xs text-zinc-500">Year {c.year}</span>}
              </div>
              <div className="truncate text-xs text-zinc-500">{c.sections.map((s) => `[${s.number}] ${sectionSummary(s, true)}`).join('  ')}</div>
            </button>
          ))}
        </div>
      </div>
    </Sheet>
  )
}
