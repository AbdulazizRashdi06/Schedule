import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { KIND_NAME, type Group, type GroupKind, type GroupSummary } from '../lib/groups'
import { Button, ErrorText, Sk, TextField, cx } from '../components/ui'
import { Icon, RoundButton, useBack } from './timetable'

/** Your groups, and making a new one: friends to find free time, or a car to plan rides. */
export default function Groups() {
  const back = useBack('/')
  const navigate = useNavigate()
  const [groups, setGroups] = useState<GroupSummary[] | null>(null)
  const [error, setError] = useState('')
  const [making, setMaking] = useState(false)
  const [name, setName] = useState('')
  const [kind, setKind] = useState<GroupKind>('friends')
  const [me, setMe] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<GroupSummary[]>('/me/groups').then(setGroups, (e) => setError(errorText(e)))
  }, [])

  const create = async () => {
    setBusy(true)
    setError('')
    try {
      const g = await api.post<Group>('/me/groups', { name, kind, displayName: me })
      navigate(`/groups/${g.id}?invite=1`)
    } catch (e) {
      setError(errorText(e))
      setBusy(false)
    }
  }

  return (
    <div className="min-h-dvh bg-cream text-navy">
      <div className="mx-auto flex max-w-[480px] flex-col gap-6 px-6 pt-[max(1.75rem,env(safe-area-inset-top))] pb-10">
        <RoundButton label="Back" onClick={back} className="border-[2.5px] border-navy">
          <Icon name="back" />
        </RoundButton>
        <div className="flex flex-col gap-2">
          <h1 className="display text-[56px] leading-[0.86] tracking-[-0.05em]">Groups.</h1>
          <p className="text-[16px] font-semibold text-muted">See when your friends are all free, or plan rides with the people you drive to uni with.</p>
        </div>

        {!groups ? (
          <div aria-busy="true" className="flex flex-col gap-2.5">
            <Sk className="h-[76px] rounded-[20px]" />
            <Sk className="h-[76px] rounded-[20px]" />
          </div>
        ) : groups.length === 0 && !making ? (
          <p className="text-[16px] font-semibold text-muted">No groups yet. Make one and send the invite link to your friends.</p>
        ) : (
          <ul className="flex flex-col gap-2.5">
            {groups.map((g) => (
              <li key={g.id}>
                <Link to={`/groups/${g.id}`} className="press flex items-center justify-between gap-3 rounded-[20px] bg-paper px-5 py-4 shadow-[0_1px_0_rgb(11_25_87/0.06),0_8px_20px_-14px_rgb(11_25_87/0.35)]">
                  <span className="flex min-w-0 flex-col">
                    <span className="truncate text-[19px] font-extrabold">{g.name}</span>
                    <span className="label text-[11px] text-muted">
                      {KIND_NAME[g.kind]} · {g.members} {g.members === 1 ? 'person' : 'people'}
                    </span>
                  </span>
                  <Icon name="next" />
                </Link>
              </li>
            ))}
          </ul>
        )}

        {making ? (
          <section className="flex flex-col gap-3.5 rounded-[20px] p-4 shadow-[inset_0_0_0_2px_rgb(0_0_0/0.1)]">
            <h2 className="text-[20px] font-extrabold">New group</h2>
            <div role="radiogroup" aria-label="Kind of group" className="grid grid-cols-2 gap-2">
              {(
                [
                  ['friends', 'Friends', 'When are we all free?'],
                  ['car', 'Car', 'When do we leave?'],
                ] as const
              ).map(([id, label, hint]) => (
                <button
                  key={id}
                  type="button"
                  role="radio"
                  aria-checked={kind === id}
                  onClick={() => setKind(id)}
                  className={cx('press flex flex-col items-start rounded-[16px] px-4 py-2.5 text-left', kind === id ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
                >
                  <span className="text-[16px] font-extrabold">{label}</span>
                  <span className={cx('text-[12px] font-semibold', kind === id ? 'text-ice' : 'text-muted')}>{hint}</span>
                </button>
              ))}
            </div>
            <TextField label="Group name" value={name} maxLength={40} placeholder={kind === 'car' ? 'Morning car' : 'Study group'} onChange={(e) => setName(e.target.value)} />
            <TextField label="Your name in the group" value={me} maxLength={30} placeholder="What your friends call you" onChange={(e) => setMe(e.target.value)} />
            <p className="text-[13px] font-semibold text-muted">The group sees this name, never your student ID.</p>
            <ErrorText>{error}</ErrorText>
            <div className="flex gap-2.5">
              <Button variant="secondary" onClick={() => setMaking(false)}>
                Cancel
              </Button>
              <Button busy={busy} disabled={!name.trim() || !me.trim()} onClick={create}>
                Make group
              </Button>
            </div>
          </section>
        ) : (
          <>
            <ErrorText>{error}</ErrorText>
            <button type="button" onClick={() => setMaking(true)} className="press h-[56px] rounded-full bg-navy text-[17px] font-extrabold text-cream">
              New group
            </button>
          </>
        )}
      </div>
    </div>
  )
}
