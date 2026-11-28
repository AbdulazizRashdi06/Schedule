import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ApiError, api, errorText } from '../lib/api'
import { DAY_SHORT, fmtRange } from '../lib/time'
import { TYPE_NAME, fmtDate, type ClassType } from '../lib/slots'
import type { TeacherPage as Page } from '../lib/types'
import { ErrorText, Loading, Tag, cx } from '../components/ui'
import { officeHourText } from './ClassTeachers'
import { BackButton, TimeText, useBack } from './timetable'

const STATUS: Record<NonNullable<Page['classes'][number]['status']>, string> = { cancelled: 'Cancelled', moved: 'Moved', room: 'Room changed', extra: 'Extra class' }

/**
 * A teacher as students see them (/teacher/:key): office hours and details when they use Classy, and their classes
 * this week with any they cancelled, moved or added. A teacher who never signed in still has a page with their classes.
 */
export default function TeacherPage() {
  const { key } = useParams()
  const back = useBack('/')
  const [page, setPage] = useState<Page | null>(null)
  const [error, setError] = useState('')
  const [missing, setMissing] = useState(false)

  useEffect(() => {
    let live = true
    api.get<Page>(`/teachers/${encodeURIComponent(key ?? '')}`).then(
      (p) => live && setPage(p),
      (e) => {
        if (!live) return
        if (e instanceof ApiError && e.status === 404) setMissing(true)
        setError(errorText(e))
      },
    )
    return () => {
      live = false
    }
  }, [key])

  const days = page ? [...new Set(page.classes.map((c) => c.date))] : []
  const day = (iso: string) => {
    const d = new Date(`${iso}T12:00`)
    return `${DAY_SHORT[d.getDay()]} ${fmtDate(d)}`
  }

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex min-h-dvh max-w-[480px] flex-col px-6 pt-[max(1.25rem,env(safe-area-inset-top))] pb-[max(1.75rem,env(safe-area-inset-bottom))]">
        <header className="flex items-center justify-between gap-3 pt-1">
          <BackButton onClick={back} />
          <span className="label text-[12px] tracking-[0.14em]">Teacher</span>
        </header>

        {!page ? (
          error ? (
            <div className="mt-8 flex flex-col gap-2">
              {missing && <h1 className="display text-[44px] leading-[0.9] tracking-[-0.04em]">No page here.</h1>}
              <ErrorText>{error}</ErrorText>
            </div>
          ) : (
            <Loading rows={4} label="Loading the teacher’s page" />
          )
        ) : (
          <>
            <h1 className="display mt-6 text-[52px] leading-[0.88] tracking-[-0.04em] text-balance break-words hyphens-auto">{page.name}</h1>
            <p className="mt-3">
              <Tag tone={page.hasAccount ? 'navy' : 'ice'}>{page.hasAccount ? 'Uses Classy' : 'Hasn’t joined Classy yet'}</Tag>
            </p>

            {page.hasAccount && (
              <section aria-label="Office hours and details" className="mt-6 flex flex-col gap-3 rounded-[24px] bg-navy p-5 text-cream">
                <div className="flex flex-col gap-1">
                  <h2 className="text-[18px] font-extrabold">Office hours</h2>
                  {page.officeHours.length === 0 ? (
                    <p className="text-[14px] font-semibold text-ice">None listed yet.</p>
                  ) : (
                    <>
                      <ul className="flex flex-col gap-1.5 pt-1">
                        {page.officeHours.map((h, i) => (
                          <li key={i} className="rounded-[14px] bg-navy-800 px-3.5 py-2.5 text-[16px] font-extrabold tabular-nums">
                            {officeHourText(h)}
                          </li>
                        ))}
                      </ul>
                      <p className="pt-1 text-[13px] font-semibold text-ice">Not on holidays or outside teaching weeks.</p>
                    </>
                  )}
                </div>
                {(page.officeRoom || page.note || page.email) && (
                  <dl className="flex flex-col gap-2.5 border-t-[1.5px] border-navy-line pt-3 text-[15px] font-semibold">
                    {page.officeRoom && (
                      <div className="flex flex-col gap-0.5">
                        <dt className="label text-[10px] text-ice">Office</dt>
                        <dd>{page.officeRoom}</dd>
                      </div>
                    )}
                    {page.note && (
                      <div className="flex flex-col gap-0.5">
                        <dt className="label text-[10px] text-ice">Note</dt>
                        <dd className="whitespace-pre-wrap">{page.note}</dd>
                      </div>
                    )}
                    {page.email && (
                      <div className="flex flex-col gap-0.5">
                        <dt className="label text-[10px] text-ice">Email</dt>
                        <dd>
                          <a href={`mailto:${page.email}`} className="break-all underline underline-offset-[3px]">
                            {page.email}
                          </a>
                        </dd>
                      </div>
                    )}
                  </dl>
                )}
              </section>
            )}

            <section aria-label="This week" className="mt-7 flex flex-col">
              <div className="mb-2 flex items-baseline justify-between gap-3">
                <h2 className="display text-[22px] tracking-[-0.02em]">This week</h2>
                <span className="label text-[11px] text-muted">
                  {day(page.week.from)} – {day(page.week.to)}
                </span>
              </div>
              {page.classes.length === 0 && <p className="border-t-[2.5px] border-navy py-3.5 text-[16px] font-semibold text-muted">No classes this week.</p>}
              {days.map((iso) => (
                <div key={iso} className="border-t-[2.5px] border-navy pt-2.5 pb-1">
                  <h3 className="label text-[11px] tracking-[0.12em] text-muted">{day(iso)}</h3>
                  <ul>
                    {page.classes
                      .filter((c) => c.date === iso)
                      .map((c, i) => {
                        const off = c.status === 'cancelled'
                        return (
                          <li key={i} className="grid grid-cols-[76px_minmax(0,1fr)] items-start gap-3 border-b-[1.5px] border-navy/10 py-3 last:border-b-0">
                            <span className={cx('display text-[28px] leading-none tracking-[-0.05em] tabular-nums', off && 'text-navy/45 line-through decoration-[3px]')}><TimeText t={c.start} stack /></span>
                            <span className="flex min-w-0 flex-col gap-0.5">
                              <span className="flex flex-wrap items-center gap-1.5">
                                <span className={cx('text-[17px] leading-[1.15] font-extrabold', off && 'text-navy/55')}>{c.name}</span>
                                {c.status && (
                                  <span className={cx('label rounded-full px-2 py-0.5 text-[9px]', off ? 'bg-navy text-cream' : 'bg-ice text-navy shadow-[inset_0_0_0_1.5px_var(--color-navy)]')}>{STATUS[c.status]}</span>
                                )}
                              </span>
                              <span className="font-mono text-[12px] uppercase">
                                {c.codes.join(' · ')}
                                {c.codes.length === 1 && c.section ? ` sec ${c.section}` : ''}
                              </span>
                              <span className="font-mono text-[12px] uppercase">
                                {fmtRange(c.start, c.end)} · {TYPE_NAME[c.type as ClassType] ?? c.type}, {c.room || 'Room TBA'}
                              </span>
                              {c.note && <span className="text-[14px] font-semibold text-muted">{c.note}</span>}
                            </span>
                          </li>
                        )
                      })}
                  </ul>
                </div>
              ))}
            </section>
          </>
        )}
      </div>
    </div>
  )
}
