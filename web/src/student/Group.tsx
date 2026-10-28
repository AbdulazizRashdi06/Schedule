import { useEffect, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { DAY_SHORT } from '../lib/time'
import { addDays, fmtDate, fmtDuration, fmtWeekRange, isoDate, weekStart, weekTitle } from '../lib/slots'
import { KIND_NAME, inviteLink, type Group as GroupInfo, type GroupWeek } from '../lib/groups'
import type { Me } from '../lib/types'
import { Button, Checkbox, Confirm, ErrorText, Sk, TextField, Toast, cx, useToast } from '../components/ui'
import { Icon, RoundButton, useBack, useNow } from './timetable'
import Rides from './Rides'

const MIN_OPTIONS = [30, 60, 90] as const

/** One group: when everyone is free this week (and who's busy when nearly everyone is), its people, the invite link. */
export default function Group() {
  const { id } = useParams()
  const back = useBack('/groups')
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const now = useNow()
  const toast = useToast()
  const [group, setGroup] = useState<GroupInfo | null>(null)
  const [week, setWeek] = useState<GroupWeek | null>(null)
  const [me, setMe] = useState<Me | null>(null)
  const [error, setError] = useState('')
  const [atLeast, setAtLeast] = useState<number>(30)
  const [leaving, setLeaving] = useState(false)
  const [tab, setTab] = useState<'rides' | 'free'>('rides')

  const offset = Number(params.get('w')) || 0
  const start = weekStart(now, offset)
  const startIso = isoDate(start)

  useEffect(() => {
    api.get<GroupInfo>(`/me/groups/${id}`).then(setGroup, (e) => setError(errorText(e)))
    api.get<Me>('/me').then(setMe, () => {})
  }, [id])
  useEffect(() => {
    setWeek(null)
    api.get<GroupWeek>(`/me/groups/${id}/week?start=${startIso}`).then(setWeek, (e) => setError(errorText(e)))
  }, [id, startIso])

  const go = (w: number) => {
    const p = new URLSearchParams(params)
    p.set('w', String(w))
    p.delete('invite')
    setParams(p, { replace: true })
  }

  const share = async () => {
    if (!group) return
    const url = inviteLink(group.inviteCode)
    try {
      if (navigator.share) await navigator.share({ title: `Join ${group.name} on Classy`, url })
      else {
        await navigator.clipboard.writeText(url)
        toast.show('Invite link copied')
      }
    } catch {
      /* closed the share sheet */
    }
  }

  if (error && !group)
    return (
      <div className="min-h-dvh bg-cream px-6 pt-10 text-navy">
        <ErrorText>{error}</ErrorText>
      </div>
    )

  const alone = group?.members.length === 1
  const shownDays = week?.days ?? []

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex max-w-[480px] flex-col gap-6 px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-12">
        <RoundButton label="Back" onClick={back} className="border-[2.5px] border-navy">
          <Icon name="back" />
        </RoundButton>
        <div className="flex flex-col gap-1.5">
          <span className="label text-[11px] text-muted">
            {group ? `${KIND_NAME[group.kind]} · ${group.members.length} ${group.members.length === 1 ? 'person' : 'people'}` : ' '}
          </span>
          <h1 className="display text-[48px] leading-[0.88] tracking-[-0.05em] break-words">{group?.name ?? ' '}</h1>
        </div>

        {group && (alone || params.get('invite')) && (
          <section className="flex flex-col gap-2.5 rounded-[20px] bg-navy p-5 text-cream">
            <h2 className="text-[20px] font-extrabold">Invite your friends</h2>
            <p className="text-[14px] font-semibold text-ice">Send them the link. They sign in with their student ID and pick a name for the group.</p>
            <button type="button" onClick={share} className="press h-12 self-start rounded-full bg-sky px-5 text-[15px] font-extrabold text-navy">
              Share invite link
            </button>
          </section>
        )}

        <div className="flex items-center justify-between gap-2">
          <RoundButton label="Previous week" onClick={() => go(offset - 1)} className="size-11 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]">
            <Icon name="prev" />
          </RoundButton>
          <div className="min-w-0 text-center">
            <p className="display text-[28px] leading-[0.9] tracking-[-0.03em]">{weekTitle(offset, start, me?.semester?.weeks)}</p>
            <p className="label mt-1.5 text-[10px] text-muted">{fmtWeekRange(start, 5)}</p>
          </div>
          <RoundButton label="Next week" onClick={() => go(offset + 1)} className="size-11 bg-navy text-cream">
            <Icon name="next" />
          </RoundButton>
        </div>

        {group?.kind === 'car' && (
          <div role="tablist" aria-label="Show" className="label grid grid-cols-2 gap-0.5 rounded-full p-1 text-[11px] shadow-[inset_0_0_0_2px_rgb(11_25_87/0.14)]">
            {(
              [
                ['rides', 'Rides'],
                ['free', 'Free time'],
              ] as const
            ).map(([t, label]) => (
              <button key={t} type="button" role="tab" aria-selected={tab === t} onClick={() => setTab(t)} className={cx('press h-10 rounded-full', tab === t ? 'bg-navy text-cream' : '')}>
                {label}
              </button>
            ))}
          </div>
        )}

        {group?.kind === 'car' && tab === 'rides' ? (
          <Rides group={group} start={start} startIso={startIso} onSaved={setGroup} />
        ) : (
        <div className="flex flex-col gap-3">
          <div className="flex items-center justify-between gap-3">
            <h2 className="text-[22px] font-extrabold tracking-[-0.02em]">Everyone free</h2>
            <div role="radiogroup" aria-label="At least" className="label flex gap-0.5 rounded-full p-1 text-[10px] shadow-[inset_0_0_0_2px_rgb(11_25_87/0.14)]">
              {MIN_OPTIONS.map((m) => (
                <button key={m} type="button" role="radio" aria-checked={atLeast === m} onClick={() => setAtLeast(m)} className={cx('press h-8 rounded-full px-2.5', atLeast === m ? 'bg-navy text-cream' : '')}>
                  {fmtDuration(m)}+
                </button>
              ))}
            </div>
          </div>
          {!week ? (
            <div aria-busy="true" className="flex flex-col gap-2">
              {[0, 1, 2].map((i) => (
                <Sk key={i} className="h-[64px] rounded-[16px]" />
              ))}
            </div>
          ) : alone ? (
            <p className="text-[15px] font-semibold text-muted">Free times show here once someone joins.</p>
          ) : (
            shownDays.map((d) => {
              const free = d.free.filter((w) => w.minutes >= atLeast)
              const nearly = d.nearly.filter((w) => w.minutes >= atLeast)
              return (
                <section key={d.date} className="flex flex-col gap-1.5 border-t-[2.5px] border-navy pt-2.5">
                  <h3 className="label text-[12px]">
                    {DAY_SHORT[d.day]} {fmtDate(addDays(start, d.day))}
                  </h3>
                  {free.length === 0 && nearly.length === 0 && <p className="text-[14px] font-semibold text-muted">No shared free time.</p>}
                  {free.map((w) => (
                    <p key={w.start} className="flex items-baseline justify-between gap-3 rounded-[14px] bg-sky px-3.5 py-2.5">
                      <span className="display text-[22px] tracking-[-0.03em] tabular-nums">
                        {w.start}–{w.end}
                      </span>
                      <span className="label text-[11px]">{fmtDuration(w.minutes)} · all free</span>
                    </p>
                  ))}
                  {nearly.map((w) => (
                    <p key={w.start} className="flex items-baseline justify-between gap-3 rounded-[14px] bg-ice px-3.5 py-2">
                      <span className="text-[16px] font-extrabold tabular-nums">
                        {w.start}–{w.end}
                      </span>
                      <span className="text-right text-[13px] font-semibold text-muted">
                        {fmtDuration(w.minutes)} · {w.busy.join(' and ')} busy
                      </span>
                    </p>
                  ))}
                  {d.classes.length > 0 && (
                    <details className="text-[13px] font-semibold text-muted">
                      <summary className="cursor-pointer py-1">Shown classes</summary>
                      <ul className="flex flex-col gap-0.5 pl-1">
                        {d.classes.map((c, i) => (
                          <li key={i}>
                            {c.member}: {c.label} {c.start}–{c.end}
                            {c.room && `, ${c.room}`}
                          </li>
                        ))}
                      </ul>
                    </details>
                  )}
                </section>
              )
            })
          )}
        </div>
        )}

        {group && <MeCard group={group} onSaved={setGroup} />}

        {group && (
          <section className="flex flex-col gap-2">
            <h2 className="text-[20px] font-extrabold">People</h2>
            <ul className="flex flex-wrap gap-1.5">
              {group.members.map((m) => (
                <li key={m.id} className={cx('rounded-full px-3 py-1.5 text-[14px] font-bold', m.me ? 'bg-navy text-cream' : 'bg-ice')}>
                  {m.name}
                  {m.me && ' (you)'}
                </li>
              ))}
            </ul>
            <div className="flex flex-wrap gap-2.5 pt-2">
              <Button variant="secondary" size="sm" onClick={share}>
                Invite link
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={async () => {
                  const r = await api.post<{ inviteCode: string }>(`/me/groups/${group.id}/invite`)
                  setGroup({ ...group, inviteCode: r.inviteCode })
                  toast.show('New link made. The old one stops working.')
                }}
              >
                New link
              </Button>
              <Button variant="danger" size="sm" onClick={() => setLeaving(true)}>
                Leave group
              </Button>
            </div>
            <Confirm
              open={leaving}
              title={`Leave ${group.name}?`}
              body={alone ? 'You’re the last one in it, so the group is deleted.' : 'You can join again with an invite link.'}
              confirmLabel="Leave"
              danger
              onClose={() => setLeaving(false)}
              onConfirm={async () => {
                await api.del(`/me/groups/${group.id}/me`)
                navigate('/groups', { replace: true })
              }}
            />
          </section>
        )}
      </div>
      <Toast toast={toast} className="bottom-6 left-1/2 -translate-x-1/2" />
    </div>
  )
}

/** Your name in the group, and whether the group sees your classes or just when you're busy. */
function MeCard({ group, onSaved }: { group: GroupInfo; onSaved: (g: GroupInfo) => void }) {
  const mine = group.members.find((m) => m.me)
  const [name, setName] = useState(mine?.name ?? '')
  const [show, setShow] = useState(mine?.showClasses ?? false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const changed = name !== mine?.name || show !== mine?.showClasses
  return (
    <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
      <h2 className="text-[20px] font-extrabold">You in this group</h2>
      <TextField label="Your name" value={name} maxLength={30} onChange={(e) => setName(e.target.value)} />
      <Checkbox checked={show} onChange={setShow} label="Show my classes to the group" />
      <p className="text-[13px] font-semibold text-muted">Off: they only see when you’re busy. Your own slots always show as “Busy”.</p>
      <ErrorText>{error}</ErrorText>
      {changed && (
        <Button
          className="self-start"
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              onSaved(await api.put<GroupInfo>(`/me/groups/${group.id}/me`, { displayName: name, showClasses: show }))
            } catch (e) {
              setError(errorText(e))
            } finally {
              setBusy(false)
            }
          }}
        >
          Save
        </Button>
      )}
    </section>
  )
}
