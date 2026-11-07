import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Sheet } from '../components/ui'

/**
 * "Add to my calendar": the student's private calendar feed, with one button per calendar app. Each app subscribes to
 * the link and keeps up with changes on its own schedule, so Classy still shows a change first.
 */
export default function CalendarSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState(false)
  const [resetting, setResetting] = useState(false)

  useEffect(() => {
    if (!open || url) return
    api.get<{ url: string }>('/me/calendar').then((r) => setUrl(r.url), (e) => setError(errorText(e)))
  }, [open, url])

  const webcal = url?.replace(/^https?:\/\//, 'webcal://') ?? ''
  const apps = url
    ? [
        { name: 'Apple Calendar', href: webcal, hint: 'iPhone, iPad and Mac. Opens Calendar to subscribe.' },
        { name: 'Google Calendar', href: `https://calendar.google.com/calendar/r?cid=${encodeURIComponent(webcal)}`, hint: 'Then it appears on every phone signed in to that Google account.' },
        { name: 'Outlook', href: `https://outlook.office.com/calendar/0/addfromweb?url=${encodeURIComponent(url)}&name=Classy`, hint: 'Your university Microsoft account.' },
        // Samsung Calendar shows the calendars of the Google account on the phone, so it's added through Google.
        { name: 'Samsung Calendar', href: `https://calendar.google.com/calendar/r?cid=${encodeURIComponent(webcal)}`, hint: 'Adds it to your Google account. Then in Samsung Calendar, open Manage calendars and turn on Classy.' },
      ]
    : []

  return (
    <Sheet open={open} onClose={onClose} label="Add to my calendar">
      <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Add to my calendar</h2>
      <p className="text-[16px] leading-snug font-semibold text-muted">
        Your classes and your own slots, kept up to date in your calendar app. Calendars refresh on their own schedule, so a last-minute change shows in Classy first.
      </p>
      <ErrorText>{error}</ErrorText>
      {!url && !error && <p className="label py-4 text-[11px] text-muted">Making your link…</p>}
      {url && (
        <>
          <ul className="flex flex-col">
            {apps.map((a) => (
              <li key={a.name}>
                <a href={a.href} target={a.href.startsWith('webcal:') ? undefined : '_blank'} rel="noreferrer" className="tint flex items-center justify-between gap-3 border-t-[1.5px] border-navy/10 py-3.5">
                  <span className="flex min-w-0 flex-col gap-0.5">
                    <span className="text-[20px] leading-tight font-extrabold">{a.name}</span>
                    <span className="text-[13px] font-semibold text-muted">{a.hint}</span>
                  </span>
                  <span aria-hidden="true" className="text-[18px]">→</span>
                </a>
              </li>
            ))}
          </ul>
          <div className="flex flex-col gap-1.5 border-t-[1.5px] border-navy/10 pt-3.5">
            <span className="label text-[11px] tracking-[0.12em]">Any other calendar · paste this link</span>
            <code className="rounded-[12px] bg-paper px-3 py-2.5 font-mono text-[12px] font-bold break-all select-all">{url}</code>
            <div className="flex flex-wrap gap-2 pt-1">
              <Button
                variant="secondary"
                size="sm"
                onClick={async () => {
                  try {
                    await navigator.clipboard.writeText(url)
                    setCopied(true)
                  } catch {
                    setError('Copy didn’t work here. Select the link and copy it.')
                  }
                }}
              >
                {copied ? 'Copied' : 'Copy link'}
              </Button>
              <Button
                variant="danger"
                size="sm"
                busy={resetting}
                onClick={async () => {
                  setResetting(true)
                  setError('')
                  try {
                    const r = await api.post<{ url: string }>('/me/calendar/reset')
                    setUrl(r.url)
                    setCopied(false)
                  } catch (e) {
                    setError(errorText(e))
                  } finally {
                    setResetting(false)
                  }
                }}
              >
                {resetting ? 'Resetting…' : 'Reset link'}
              </Button>
            </div>
            <p className="text-[13px] font-semibold text-muted">The link is private. Reset it if you shared it by mistake: calendars using the old one stop updating.</p>
          </div>
        </>
      )}
    </Sheet>
  )
}
