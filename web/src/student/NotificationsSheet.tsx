import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { ENABLE_MESSAGE, deviceSubscribed, disableDevice, enableDevice, useNotifySettings } from '../lib/push'
import { Button, Checkbox, ErrorText, Sheet, cx } from '../components/ui'

const CHOICES = [null, 5, 10, 15, 30, 60] as const

/**
 * Notifications: turn them on for this phone or browser, how long before class to be reminded, and whether to hear
 * when a class changes. The two choices apply to every device the student turned on.
 */
export default function NotificationsSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { settings, save, reload } = useNotifySettings()
  const [device, setDevice] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    setStatus('')
    setError('')
    void reload()
    deviceSubscribed().then(setDevice, () => setDevice(false))
  }, [open, reload])

  const run = async (fn: () => Promise<void>) => {
    setBusy(true)
    setError('')
    setStatus('')
    try {
      await fn()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const turnOn = () =>
    run(async () => {
      const r = await enableDevice()
      if (r !== 'on') return setError(ENABLE_MESSAGE[r])
      setDevice(true)
      // Turning notifications on is asking for reminders: start at 15 minutes unless already chosen.
      if (settings && settings.remindMinutes === null) await save({ remindMinutes: 15, changes: settings.changes })
      await reload()
      setStatus('On. You’ll get a reminder before each class.')
    })

  return (
    <Sheet open={open} onClose={onClose} label="Notifications">
      <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Notifications</h2>
      <p className="text-[16px] leading-snug font-semibold text-muted">A reminder before each class, and a heads-up when one of your classes moves.</p>

      <div className="flex flex-wrap items-center gap-2 border-t-[1.5px] border-navy/10 pt-3.5">
        {device ? (
          <>
            <span className="mr-auto text-[17px] font-extrabold">On for this device</span>
            <Button variant="secondary" size="sm" busy={busy} onClick={() => run(async () => (await api.post('/me/push/test'), setStatus('Sent. It should arrive in a few seconds.')))}>
              Send a test
            </Button>
            <Button variant="ghost" size="sm" onClick={() => run(async () => (await disableDevice(), setDevice(false), await reload()))}>
              Turn off
            </Button>
          </>
        ) : (
          <Button className="w-full" busy={busy} disabled={device === null} onClick={turnOn}>
            Turn on notifications
          </Button>
        )}
      </div>
      <p className="min-h-5 text-[14px] font-bold" role="status">
        {status}
      </p>
      <ErrorText>{error}</ErrorText>

      {settings && (
        <div className={cx('flex flex-col gap-4 transition-opacity duration-200', !device && 'opacity-50')}>
          <div className="flex flex-col gap-2">
            <span className="label text-[11px] tracking-[0.12em]">Remind me before class</span>
            <div role="radiogroup" aria-label="Remind me before class" className="flex flex-wrap gap-1.5">
              {CHOICES.map((m) => {
                const on = settings.remindMinutes === m
                return (
                  <button
                    key={m ?? 'off'}
                    type="button"
                    role="radio"
                    aria-checked={on}
                    onClick={() => run(() => save({ remindMinutes: m, changes: settings.changes }))}
                    className={cx('press label h-10 rounded-full px-4 text-[12px]', on ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.2)]')}
                  >
                    {m === null ? 'Off' : `${m} min`}
                  </button>
                )
              })}
            </div>
          </div>
          <Checkbox checked={settings.changes} onChange={(v) => void run(() => save({ remindMinutes: settings.remindMinutes, changes: v }))} label="When one of my classes changes" />
          {settings.devices > 1 && <p className="text-[13px] font-semibold text-muted">On for {settings.devices} devices. These choices apply to all of them.</p>}
        </div>
      )}
    </Sheet>
  )
}
