import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import type { Course, CourseType, Major, Me, Options, PickRow } from '../lib/types'
import { BackIcon, Button, Card, ErrorText, Loading, Notice, cx } from '../components/ui'
import { CourseRow, clashesFor, type Picks } from '../components/CourseRow'
import { sectionSummary } from '../lib/time'
import { StudentShell } from './StudentShell'

/** Steps on the progress bar. "editcore" is a sub-page of core. */
const STEPS = ['major', 'year', 'core', 'ls', 'electives', 'done'] as const
type View = (typeof STEPS)[number] | 'editcore' | 'waiting'

export default function Setup() {
  const navigate = useNavigate()
  const [me, setMe] = useState<Me | null>(null)
  const [view, setView] = useState<View>('major')
  const [error, setError] = useState('')

  useEffect(() => {
    api.get<Me>('/me').then((m) => {
      if (m.setupDone) return navigate('/', { replace: true })
      setMe(m)
      setView(m.step as View)
    }, (e) => setError(errorText(e)))
  }, [navigate])

  if (error) return <StudentShell><ErrorText>{error}</ErrorText></StudentShell>
  if (!me) return <StudentShell><Loading /></StudentShell>

  const go = async (next: View, persist = true) => {
    setError('')
    if (persist && (STEPS as readonly string[]).includes(next) && next !== 'done' && me.semester) {
      await api.put('/me/step', { step: next }).catch(() => {})
    }
    setView(next)
    window.scrollTo(0, 0)
  }

  const reload = async () => {
    const m = await api.get<Me>('/me')
    setMe(m)
    return m
  }

  return (
    <SetupFrame view={view} onBack={view === 'major' || view === 'waiting' ? undefined : () => go(view === 'editcore' ? 'core' : STEPS[STEPS.indexOf(view as (typeof STEPS)[number]) - 1])}>
      {view === 'major' && <MajorStep me={me} onNext={async () => { await reload(); go('year', false) }} />}
      {view === 'year' && <YearStep me={me} onNext={async () => { const m = await reload(); setView(m.semester ? 'core' : 'waiting'); window.scrollTo(0, 0) }} />}
      {view === 'waiting' && <Waiting me={me} onChange={() => go('major', false)} />}
      {(view === 'core' || view === 'editcore' || view === 'ls' || view === 'electives' || view === 'done') && (
        <PickSteps me={me} view={view} go={go} />
      )}
    </SetupFrame>
  )
}

function SetupFrame({ view, onBack, children }: { view: View; onBack?: () => void; children: React.ReactNode }) {
  const index = view === 'editcore' ? 2 : view === 'waiting' ? 2 : STEPS.indexOf(view)
  return (
    <StudentShell
      title={
        <div className="flex items-center gap-2">
          {onBack ? (
            <button onClick={onBack} className="-ml-2 rounded-lg p-2 text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-800" aria-label="Back">
              <BackIcon />
            </button>
          ) : (
            <span className="w-1" />
          )}
          <span className="text-sm text-zinc-500">
            {view === 'waiting' ? 'Almost there' : `Step ${Math.min(index + 1, STEPS.length)} of ${STEPS.length}`}
          </span>
        </div>
      }
    >
      <div className="mb-6 h-1.5 overflow-hidden rounded-full bg-zinc-200 dark:bg-zinc-800">
        <div className="h-full rounded-full bg-indigo-600 transition-all duration-300 dark:bg-indigo-500" style={{ width: `${((index + 1) / STEPS.length) * 100}%` }} />
      </div>
      {children}
    </StudentShell>
  )
}

function Heading({ title, sub }: { title: string; sub?: React.ReactNode }) {
  return (
    <div className="mb-5 space-y-1">
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      {sub && <p className="text-[15px] text-zinc-500">{sub}</p>}
    </div>
  )
}

function BottomBar({ children }: { children: React.ReactNode }) {
  return (
    <div className="sticky bottom-0 -mx-5 mt-6 border-t border-zinc-200/70 bg-zinc-50/90 px-5 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] backdrop-blur dark:border-zinc-800 dark:bg-zinc-950/90">
      <div className="flex gap-2">{children}</div>
    </div>
  )
}

function MajorStep({ me, onNext }: { me: Me; onNext: () => void }) {
  const [majors, setMajors] = useState<Major[] | null>(null)
  const [picked, setPicked] = useState<number | null>(me.major?.id ?? null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    api.get<Major[]>('/majors').then(setMajors, (e) => setError(errorText(e)))
  }, [])
  return (
    <>
      <Heading title="What's your major?" />
      {!majors ? (
        <Loading />
      ) : majors.length === 0 ? (
        <Notice>No majors have been set up yet. Check back soon.</Notice>
      ) : (
        <div className="grid gap-2.5" role="radiogroup">
          {majors.map((m) => (
            <Card
              key={m.id}
              onClick={() => setPicked(m.id)}
              className={cx('flex items-center gap-3 p-4', picked === m.id && 'ring-2! ring-indigo-600! dark:ring-indigo-400!')}
            >
              <span className={cx('flex size-10 shrink-0 items-center justify-center rounded-xl text-sm font-semibold', picked === m.id ? 'bg-indigo-600 text-white dark:bg-indigo-500' : 'bg-zinc-100 text-zinc-600 dark:bg-zinc-800 dark:text-zinc-300')}>
                {m.code.slice(0, 4)}
              </span>
              <span className="font-medium">{m.name}</span>
            </Card>
          ))}
        </div>
      )}
      <ErrorText>{error}</ErrorText>
      <BottomBar>
        <Button
          className="flex-1"
          disabled={picked == null}
          busy={busy}
          onClick={async () => {
            setBusy(true)
            try {
              await api.put('/me/profile', { majorId: picked })
              onNext()
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          Next
        </Button>
      </BottomBar>
    </>
  )
}

function YearStep({ me, onNext }: { me: Me; onNext: () => void }) {
  // The predicted year is picked already; the student confirms with Next.
  const [year, setYear] = useState<number>(me.year ?? Math.min(me.predictedYear, 4))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <>
      <Heading title="Which year are you in?" sub={me.year == null ? `We guessed from your ID (${me.universityId}). Change it if that's not right.` : undefined} />
      <div className="grid grid-cols-4 gap-2.5" role="radiogroup">
        {[1, 2, 3, 4].map((y) => (
          <button
            key={y}
            role="radio"
            aria-checked={year === y}
            onClick={() => setYear(y)}
            className={cx(
              'flex h-20 flex-col items-center justify-center rounded-2xl ring-1 transition active:scale-[0.97]',
              year === y ? 'bg-indigo-600 text-white ring-indigo-600 dark:bg-indigo-500 dark:ring-indigo-500' : 'bg-white ring-zinc-200 dark:bg-zinc-900 dark:ring-zinc-800',
            )}
          >
            <span className={cx('text-xs', year === y ? 'text-indigo-100' : 'text-zinc-500')}>Year</span>
            <span className="text-2xl font-semibold">{y}</span>
          </button>
        ))}
      </div>
      {me.predictedYear >= 5 && <p className="mt-3 text-sm text-zinc-500">Fifth year or later? Pick Year 4 and add any other courses on the next steps.</p>}
      <ErrorText>{error}</ErrorText>
      <BottomBar>
        <Button
          className="flex-1"
          busy={busy}
          onClick={async () => {
            setBusy(true)
            try {
              await api.put('/me/profile', { year })
              onNext()
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          Next
        </Button>
      </BottomBar>
    </>
  )
}

function Waiting({ me, onChange }: { me: Me; onChange: () => void }) {
  return (
    <div className="space-y-5 pt-6 text-center">
      <div className="mx-auto flex size-16 items-center justify-center rounded-3xl bg-indigo-100 text-3xl dark:bg-indigo-950">⏳</div>
      <Heading title="You're all set for now" />
      <p className="text-[15px] text-zinc-600 dark:text-zinc-400">
        Courses for next semester aren't published yet. We'll take you to course picking as soon as they are.
      </p>
      <Card className="p-4 text-left text-sm">
        <div className="text-zinc-500">Your details</div>
        <div className="font-medium">
          {me.major?.name} · Year {me.year}
        </div>
      </Card>
      <Button variant="secondary" onClick={onChange}>
        Change major or year
      </Button>
    </div>
  )
}

/** Core, edit core, LS, electives and the summary share one set of picks. */
function PickSteps({ me, view, go }: { me: Me; view: View; go: (v: View, persist?: boolean) => Promise<void> }) {
  const [options, setOptions] = useState<Options | null>(null)
  const [picks, setPicks] = useState<Picks>(new Map())
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()

  useEffect(() => {
    Promise.all([api.get<Options>('/me/options'), api.get<PickRow[]>('/me/picks')]).then(
      ([o, p]) => {
        setOptions(o)
        setPicks(new Map(p.map((x) => [x.courseId, x.sectionId])))
      },
      (e) => setError(errorText(e)),
    )
  }, [])

  const byId = useMemo(() => new Map((options?.courses ?? []).map((c) => [c.id, c])), [options])
  if (error && !options) return <ErrorText>{error}</ErrorText>
  if (!options) return <Loading />

  const ofType = (t: CourseType) => options.courses.filter((c) => c.type === t)
  const pickedOf = (t: CourseType) => ofType(t).filter((c) => picks.has(c.id))
  const missingSection = (t?: CourseType) => [...picks].filter(([cid, sid]) => sid == null && (!t || byId.get(cid)?.type === t) && (byId.get(cid)?.sections.length ?? 0) > 0).map(([cid]) => byId.get(cid)!.code)

  const save = async (next: View) => {
    setBusy(true)
    setError('')
    try {
      const step = next === 'editcore' ? 'core' : next === 'done' ? 'done' : next
      await api.put('/me/picks', {
        picks: [...picks].map(([courseId, sectionId]) => ({ courseId, sectionId })),
        step: step === 'done' ? 'electives' : step,
      })
      await go(next, false)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const prev = new Set(options.previous)
  const row = (c: Course, note?: string) => (
    <CourseRow key={c.id} course={c} picks={picks} byId={byId} onChange={setPicks} note={note ?? (prev.has(c.code) ? 'Picked last semester' : undefined)} />
  )

  if (view === 'core') {
    const cores = pickedOf('Core')
    const missing = missingSection('Core')
    return (
      <>
        <Heading title="Your core courses" sub={`${me.major?.name}, Year ${me.year} · ${options.semester.name}. Pick a section for each.`} />
        {cores.length === 0 ? (
          <Notice>No core courses are listed for Year {me.year} this semester. Use “Edit core courses” to add any you take.</Notice>
        ) : (
          <div className="space-y-2.5">{cores.map((c) => row(c, c.year !== me.year && c.year ? `Year ${c.year}` : undefined))}</div>
        )}
        <ErrorText>{error}</ErrorText>
        <BottomBar>
          <Button variant="secondary" className="flex-1" onClick={() => save('editcore')} disabled={busy}>
            Edit core courses
          </Button>
          <Button className="flex-1" busy={busy} disabled={missing.length > 0} onClick={() => save('ls')}>
            Looks right
          </Button>
        </BottomBar>
        {missing.length > 0 && <p className="-mt-1 pb-2 text-center text-xs text-zinc-500">Pick a section for {missing.join(', ')}</p>}
      </>
    )
  }

  if (view === 'editcore') {
    const years = [...new Set(ofType('Core').map((c) => c.year ?? 0))].sort((a, b) => (a === me.year ? -1 : b === me.year ? 1 : a - b))
    return (
      <>
        <Heading title="Edit core courses" sub="Untick courses you're not taking, or add core courses from another year." />
        <div className="space-y-6">
          {years.map((y) => (
            <section key={y} className="space-y-2.5">
              <h2 className="text-sm font-semibold text-zinc-500">{y === 0 ? 'Any year' : `Year ${y}`}{y === me.year && ' · your year'}</h2>
              {ofType('Core').filter((c) => (c.year ?? 0) === y).map((c) => row(c))}
            </section>
          ))}
        </div>
        <ErrorText>{error}</ErrorText>
        <BottomBar>
          <Button className="flex-1" busy={busy} onClick={() => save('core')}>
            Done
          </Button>
        </BottomBar>
      </>
    )
  }

  if (view === 'ls' || view === 'electives') {
    const type: CourseType = view === 'ls' ? 'LS' : 'Elective'
    const list = ofType(type)
    const any = pickedOf(type).length > 0
    const missing = missingSection(type)
    return (
      <>
        <Heading
          title={view === 'ls' ? 'Life Skills' : 'Electives'}
          sub={view === 'ls' ? 'Tick any Life Skills courses you take this semester.' : `Electives open to ${me.major?.name}. Tick the ones you take.`}
        />
        {list.length === 0 ? <Notice>None are offered this semester.</Notice> : <div className="space-y-2.5">{list.map((c) => row(c))}</div>}
        <ErrorText>{error}</ErrorText>
        <BottomBar>
          {any ? (
            <Button className="flex-1" busy={busy} disabled={missing.length > 0} onClick={() => save(view === 'ls' ? 'electives' : 'done')}>
              Next
            </Button>
          ) : (
            <Button variant="secondary" className="flex-1" busy={busy} onClick={() => save(view === 'ls' ? 'electives' : 'done')}>
              Skip
            </Button>
          )}
        </BottomBar>
        {missing.length > 0 && <p className="-mt-1 pb-2 text-center text-xs text-zinc-500">Pick a section for {missing.join(', ')}</p>}
      </>
    )
  }

  // Summary
  const groups: { type: CourseType; label: string; edit: View }[] = [
    { type: 'Core', label: 'Core', edit: 'core' },
    { type: 'LS', label: 'Life Skills', edit: 'ls' },
    { type: 'Elective', label: 'Electives', edit: 'electives' },
  ]
  const missing = missingSection()
  const credits = [...picks.keys()].reduce((sum, id) => sum + (byId.get(id)?.credits ?? 0), 0)
  return (
    <>
      <Heading title="Here's your semester" sub={`${picks.size} course${picks.size === 1 ? '' : 's'}${credits ? ` · ${credits} credits` : ''}`} />
      <div className="space-y-5">
        {groups.map((g) => {
          const list = pickedOf(g.type)
          return (
            <section key={g.type}>
              <div className="mb-2 flex items-center justify-between">
                <h2 className="text-sm font-semibold text-zinc-500">{g.label}</h2>
                <button className="text-sm font-medium text-indigo-600 dark:text-indigo-400" onClick={() => go(g.edit)}>
                  Edit
                </button>
              </div>
              <Card className="divide-y divide-zinc-100 dark:divide-zinc-800">
                {list.length === 0 && <p className="p-3.5 text-sm text-zinc-500">None</p>}
                {list.map((c) => {
                  const s = c.sections.find((x) => x.id === picks.get(c.id))
                  const clash = s ? clashesFor(s, c.id, picks, byId) : []
                  return (
                    <div key={c.id} className="p-3.5">
                      <div className="flex items-baseline justify-between gap-2">
                        <span className="font-medium">
                          {c.code} <span className="font-normal text-zinc-500">{c.name}</span>
                        </span>
                        {s && <span className="shrink-0 text-sm text-zinc-500">Sec {s.number}</span>}
                      </div>
                      <div className="text-[13px] text-zinc-500">{s ? sectionSummary(s) : <span className="text-amber-700 dark:text-amber-400">No section picked</span>}</div>
                      {clash.length > 0 && <div className="text-[13px] text-amber-700 dark:text-amber-400">⚠ Clashes with {clash.join(', ')}</div>}
                    </div>
                  )
                })}
              </Card>
            </section>
          )
        })}
      </div>
      <ErrorText>{error}</ErrorText>
      <BottomBar>
        <Button
          className="flex-1"
          busy={busy}
          disabled={picks.size === 0 || missing.length > 0}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await api.post('/me/finish')
              navigate('/', { replace: true })
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          }}
        >
          Finish
        </Button>
      </BottomBar>
      {picks.size === 0 && <p className="-mt-1 pb-2 text-center text-xs text-zinc-500">Pick at least one course.</p>}
    </>
  )
}

