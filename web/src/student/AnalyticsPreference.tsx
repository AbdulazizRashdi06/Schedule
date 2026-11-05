import { useEngagement } from '../lib/engagement'
import { ErrorText } from '../components/ui'

export default function AnalyticsPreference() {
  const { consent, saving, error, change, refresh } = useEngagement()
  return <section aria-labelledby="usage-title" className="border-t-[1.5px] border-navy/10 py-4">
    <h2 id="usage-title" className="text-[18px] font-extrabold">Help improve Classy</h2>
    <p id="usage-detail" className="mt-2 text-[14px] leading-relaxed font-semibold text-muted">
      Share time spent and screens visited so we can improve the app. Optional session data is linked to your account,
      kept for 90 days, and shown to owners as totals. No notes, messages, or location. Turning this off deletes your session history.
    </p>
    <label className="mt-2 flex min-h-12 cursor-pointer items-center gap-3 py-2 text-[15px] font-bold">
      <input type="checkbox" checked={consent?.enabled ?? false} disabled={saving || !consent || (!consent.available && !consent.enabled)}
        aria-describedby="usage-detail" onChange={e => void change(e.target.checked)}
        className="size-5 shrink-0 accent-navy focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-navy disabled:cursor-wait" />
      {saving ? 'Saving your choice…' : 'Allow optional usage analytics'}
    </label>
    {!consent && !error && <p role="status" className="text-[13px] font-semibold text-muted">Loading your choice…</p>}
    {consent && !consent.available && <p className="text-[13px] font-semibold text-muted">Usage analytics is currently paused.</p>}
    <ErrorText>{error}</ErrorText>
    {error && <button type="button" onClick={refresh} className="tint mt-2 min-h-11 rounded-[12px] px-3 font-bold underline underline-offset-4">Try again</button>}
  </section>
}
