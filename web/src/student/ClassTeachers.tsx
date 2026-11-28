import { Link } from 'react-router-dom'
import { DAY_SHORT, fmtRange } from '../lib/time'
import { initials } from '../lib/slots'
import type { TeacherRef, TimetableClass } from '../lib/types'

/** "Sun 10:00–11:00 · B-204" */
export const officeHourText = (h: { day: number; start: string; end: string; room: string }) => `${DAY_SHORT[h.day]} ${fmtRange(h.start, h.end)}${h.room ? ` · ${h.room}` : ''}`

/**
 * Who teaches a class, on the navy panel of the class and session pages: the name on the university timetable, then
 * any teacher who added the class, each with their office hours and details when they have filled them in. A name
 * opens that teacher's page; a placeholder name ("CS1 CS1") is plain text.
 */
export default function ClassTeachers({ cls }: { cls: TimetableClass }) {
  // A timetable saved before teachers had pages (or an older server) has only the instructor's name.
  const teachers: TeacherRef[] = cls.teachers?.length
    ? cls.teachers
    : cls.section.instructor
      ? [{ name: cls.section.instructor, key: '', tappable: false, listed: true }]
      : []
  if (teachers.length === 0)
    return (
      <div className="flex items-center gap-3">
        <span aria-hidden="true" className="grid size-11 shrink-0 place-items-center rounded-full bg-ice text-[16px] font-extrabold text-navy">
          ?
        </span>
        <span className="flex min-w-0 flex-col gap-0.5">
          <span className="text-[16px] font-extrabold">Lecturer to be announced</span>
          <span className="label text-[11px] tracking-normal text-ice">Lecturer</span>
        </span>
      </div>
    )
  return (
    <ul className="flex flex-col gap-3.5">
      {teachers.map((t) => {
        const hours = t.officeHours ?? []
        const details = hours.length > 0 || !!t.officeRoom || !!t.note || !!t.email
        const who = (
          <>
            <span aria-hidden="true" className="grid size-11 shrink-0 place-items-center rounded-full bg-ice text-[16px] font-extrabold text-navy">
              {initials(t.name) || '?'}
            </span>
            <span className="flex min-w-0 flex-1 flex-col gap-0.5">
              <span className="text-[16px] font-extrabold break-words">{t.name}</span>
              <span className="label text-[11px] tracking-normal text-ice">{t.listed ? 'Lecturer' : 'Also teaches this class'}</span>
            </span>
          </>
        )
        return (
          <li key={t.key || t.name} className="flex flex-col gap-2.5">
            {t.tappable && t.key ? (
              <Link to={`/teacher/${t.key}`} aria-label={`${t.name}, open their page`} className="press flex items-center gap-3 rounded-[14px]">
                {who}
                <span aria-hidden="true" className="text-[16px] text-ice">
                  →
                </span>
              </Link>
            ) : (
              <div className="flex items-center gap-3">{who}</div>
            )}
            {details && (
              <dl className="flex flex-col gap-1.5 rounded-[18px] bg-navy-800 px-3.5 py-3 text-[14px] font-semibold">
                {hours.length > 0 && (
                  <div className="flex flex-col gap-0.5">
                    <dt className="label text-[10px] text-ice">Office hours</dt>
                    {hours.map((h, i) => (
                      <dd key={i} className="tabular-nums">
                        {officeHourText(h)}
                      </dd>
                    ))}
                  </div>
                )}
                {t.officeRoom && (
                  <div className="flex flex-col gap-0.5">
                    <dt className="label text-[10px] text-ice">Office</dt>
                    <dd>{t.officeRoom}</dd>
                  </div>
                )}
                {t.note && <dd className="whitespace-pre-wrap text-ice">{t.note}</dd>}
                {t.email && (
                  <dd>
                    <a href={`mailto:${t.email}`} className="break-all underline underline-offset-[3px]">
                      {t.email}
                    </a>
                  </dd>
                )}
              </dl>
            )}
          </li>
        )
      })}
    </ul>
  )
}
