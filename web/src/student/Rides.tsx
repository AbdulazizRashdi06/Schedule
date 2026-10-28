import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { DAY_SHORT } from '../lib/time'
import { addDays, fmtDate, fmtDuration } from '../lib/slots'
import type { Group, GroupRides } from '../lib/groups'
import { Button, ErrorText, Sk, TextField } from '../components/ui'

/** A car group's week: when to leave home and uni each day, who isn't coming, and who waits how long. */
export default function Rides({ group, start, startIso, onSaved }: { group: Group; start: Date; startIso: string; onSaved: (g: Group) => void }) {
  const [rides, setRides] = useState<GroupRides | null>(null)
  const [error, setError] = useState('')
  const [travel, setTravel] = useState(String(group.travelMinutes))
  const [early, setEarly] = useState(String(group.arriveEarlyMinutes))
  const [busy, setBusy] = useState(false)

  const load = () => api.get<GroupRides>(`/me/groups/${group.id}/rides?start=${startIso}`).then(setRides, (e) => setError(errorText(e)))
  useEffect(() => {
    setRides(null)
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [group.id, startIso, group.travelMinutes, group.arriveEarlyMinutes])

  const changed = travel !== String(group.travelMinutes) || early !== String(group.arriveEarlyMinutes)

  return (
    <div className="flex flex-col gap-3">
      <h2 className="text-[22px] font-extrabold tracking-[-0.02em]">Rides</h2>
      <ErrorText>{error}</ErrorText>
      {!rides ? (
        <div aria-busy="true" className="flex flex-col gap-2">
          {[0, 1, 2].map((i) => (
            <Sk key={i} className="h-[110px] rounded-[20px]" />
          ))}
        </div>
      ) : (
        rides.days.map((d) => (
          <section key={d.date} className="flex flex-col gap-2.5 border-t-[2.5px] border-navy pt-2.5">
            <h3 className="label text-[12px]">
              {DAY_SHORT[d.day]} {fmtDate(addDays(start, d.day))}
            </h3>
            {!d.ride ? (
              <p className="text-[14px] font-semibold text-muted">No classes for anyone. No ride.</p>
            ) : (
              <>
                <div className="grid grid-cols-3 gap-1.5 rounded-[18px] bg-navy p-3.5 text-cream">
                  {(
                    [
                      ['Leave home', d.ride.leaveHome],
                      ['At uni', d.ride.arrive],
                      ['Leave uni', d.ride.leaveUni],
                    ] as const
                  ).map(([label, time]) => (
                    <span key={label} className="flex flex-col gap-0.5">
                      <span className="label text-[10px] text-ice">{label}</span>
                      <span className="display text-[26px] leading-none tracking-[-0.03em] text-sky tabular-nums">{time}</span>
                    </span>
                  ))}
                </div>
                <ul className="flex flex-col gap-1 text-[14px] font-semibold">
                  {d.ride.riders.map((r) => (
                    <li key={r.name} className="flex justify-between gap-3">
                      <span className="font-extrabold">{r.name}</span>
                      <span className="text-right text-muted">
                        {r.firstClass}–{r.lastClass}
                        {r.morningWait > group.arriveEarlyMinutes && ` · waits ${fmtDuration(r.morningWait - group.arriveEarlyMinutes)} before`}
                        {r.afternoonWait > 0 && ` · ${fmtDuration(r.afternoonWait)} after`}
                      </span>
                    </li>
                  ))}
                </ul>
                {d.ride.secondRide && (
                  <p className="rounded-[14px] bg-ice px-3.5 py-2.5 text-[14px] font-semibold">
                    Long wait: {d.ride.secondRide.riders.join(' and ')} could get a second ride home at {d.ride.secondRide.leaveUni}.
                  </p>
                )}
              </>
            )}
            {d.ride && d.notComing.length > 0 && <p className="text-[13px] font-semibold text-muted">Not coming: {d.notComing.join(', ')}</p>}
          </section>
        ))
      )}

      <section className="flex flex-col gap-3 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
        <h2 className="text-[20px] font-extrabold">The drive</h2>
        <div className="grid grid-cols-2 gap-2.5">
          <TextField label="Minutes to uni" inputMode="numeric" value={travel} onChange={(e) => setTravel(e.target.value.replace(/\D/g, ''))} />
          <TextField label="Arrive early by" inputMode="numeric" value={early} onChange={(e) => setEarly(e.target.value.replace(/\D/g, ''))} />
        </div>
        <p className="text-[13px] font-semibold text-muted">Anyone in the car can change these. Own slots don’t count, only classes.</p>
        {changed && (
          <Button
            className="self-start"
            busy={busy}
            onClick={async () => {
              setBusy(true)
              setError('')
              try {
                onSaved(await api.put<Group>(`/me/groups/${group.id}/car`, { travelMinutes: Number(travel), arriveEarlyMinutes: Number(early) }))
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
    </div>
  )
}
