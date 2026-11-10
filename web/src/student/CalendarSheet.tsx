import { useEffect, useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Sheet } from '../components/ui'

/**
 * "Add to my calendar": the student's private calendar feed, with one button per calendar app. Each app subscribes to
 * the link and keeps up with changes on its own schedule, so Classy still shows a change first.
 */
interface Links {
  url: string
  /** The same timetable as plain text, for AI assistants. */
  textUrl: string
}

/** What the student pastes into ChatGPT: asks it to remember the link and read it for any question about classes. */
const aiPrompt = (link: string) =>
  `Please save this to your memory: whenever I ask about my classes, timetable, class times, rooms or what I have on a day, open ${link} and answer from it. ` +
  'It is my live university timetable from Classy, so open it again each time instead of relying on an old copy. ' +
  'It lists the time now and each day; add ?from=YYYY-MM-DD&days=N to it for other dates. It is private, so never show or share the link.'

export default function CalendarSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [url, setUrl] = useState<string | null>(null)
  const [textUrl, setTextUrl] = useState('')
  const [copiedAi, setCopiedAi] = useState(false)
  const [error, setError] = useState('')
  const [copied, setCopied] = useState(false)
  const [resetting, setResetting] = useState(false)

  useEffect(() => {
    if (!open || url) return
    api.get<Links>('/me/calendar').then(
      (r) => (setUrl(r.url), setTextUrl(r.textUrl)),
      (e) => setError(errorText(e)),
    )
  }, [open, url])

  const webcal = url?.replace(/^https?:\/\//, 'webcal://') ?? ''
  const google = `https://calendar.google.com/calendar/r?cid=${encodeURIComponent(webcal)}`
  const apps = url
    ? [
        { name: 'Apple', hint: 'iPhone, iPad, Mac', href: webcal },
        { name: 'Google', hint: 'Every phone on your account', href: google },
        { name: 'Outlook', hint: 'Uni Microsoft account', href: `https://outlook.office.com/calendar/0/addfromweb?url=${encodeURIComponent(url)}&name=Classy` },
        // Samsung Calendar shows the Google account's calendars, so it's added through Google.
        { name: 'Samsung', hint: 'Via Google, then turn on Classy', href: google },
      ]
    : []

  const copy = async (text: string, done: () => void, fail: string) => {
    try {
      await navigator.clipboard.writeText(text)
      done()
    } catch {
      setError(fail)
    }
  }

  return (
    <Sheet open={open} onClose={onClose} label="Calendar and ChatGPT">
      <div className="flex flex-col gap-1.5 pt-1">
        <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Add to my calendar</h2>
        <p className="text-[15px] leading-snug font-semibold text-muted">Your classes and slots in your calendar app. They update on their own.</p>
      </div>
      <ErrorText>{error}</ErrorText>
      {!url && !error && (
        <div className="grid grid-cols-2 gap-2" aria-hidden="true">
          {[0, 1, 2, 3].map((i) => (
            <span key={i} className="sk h-[88px] rounded-[18px] bg-navy/5" />
          ))}
        </div>
      )}
      {url && (
        <>
          <ul className="grid grid-cols-2 gap-2">
            {apps.map((a) => (
              <li key={a.name} className="min-w-0">
                <a
                  href={a.href}
                  target={a.href.startsWith('webcal:') ? undefined : '_blank'}
                  rel="noreferrer"
                  className="press flex h-[88px] flex-col justify-between rounded-[18px] p-3.5 shadow-[inset_0_0_0_2px_rgb(11_25_87/0.14)]"
                >
                  <span className="flex items-center justify-between">
                    <span className="text-[20px] leading-none font-extrabold">{a.name}</span>
                    <span aria-hidden="true" className="text-[16px]">↗</span>
                  </span>
                  <span className="text-[12px] leading-tight font-semibold text-muted">{a.hint}</span>
                </a>
              </li>
            ))}
          </ul>
          {/* Samsung Calendar can't subscribe to a link, only import a file once. Same origin, so `download` works. */}
          <a href={url} download="Classy.ics" className="tint -mt-0.5 rounded-[12px] py-1 text-[13px] leading-snug font-semibold text-muted">
            <span className="font-extrabold text-navy underline underline-offset-2">Samsung without Google? Download the file</span> and open it in Samsung Calendar. It adds your classes once and won’t update when they change.
          </a>

          {/* The same private link as plain text: saved in ChatGPT's memory, it opens it when asked about classes. */}
          <section className="mt-2 flex flex-col gap-3 rounded-[24px] bg-navy p-5 text-cream">
            <div className="flex flex-col gap-1.5">
              <span className="label text-[11px] tracking-[0.14em] text-sky">ChatGPT</span>
              <h3 className="text-[24px] leading-[1.05] font-extrabold tracking-[-0.02em]">Ask “when’s my next class?”</h3>
              <p className="text-[14px] leading-snug font-semibold text-cream/75">Copy, paste into ChatGPT and send. It remembers your timetable from then on.</p>
            </div>
            <Button variant="sky" onClick={() => copy(aiPrompt(textUrl), () => setCopiedAi(true), 'Copy didn’t work here. Try again in your browser.')}>
              {copiedAi ? 'Copied ✓' : 'Copy for ChatGPT'}
            </Button>
          </section>

          <details className="group mt-1 border-t-[1.5px] border-navy/10">
            <summary className="tint flex h-12 cursor-pointer list-none items-center justify-between text-[16px] font-extrabold [&::-webkit-details-marker]:hidden">
              Other calendar &amp; link
              <span aria-hidden="true" className="text-[14px] transition-transform group-open:rotate-90">→</span>
            </summary>
            <div className="flex flex-col gap-2.5 pb-1">
              <code className="rounded-[12px] bg-paper px-3 py-2.5 font-mono text-[12px] font-bold break-all select-all">{url}</code>
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" size="sm" onClick={() => copy(url, () => setCopied(true), 'Copy didn’t work here. Select the link and copy it.')}>
                  {copied ? 'Copied ✓' : 'Copy link'}
                </Button>
                <Button
                  variant="danger"
                  size="sm"
                  busy={resetting}
                  onClick={async () => {
                    setResetting(true)
                    setError('')
                    try {
                      const r = await api.post<Links>('/me/calendar/reset')
                      setUrl(r.url)
                      setTextUrl(r.textUrl)
                      setCopied(false)
                      setCopiedAi(false)
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
              <p className="text-[13px] leading-snug font-semibold text-muted">
                Private to you. Shared it by mistake? Reset it: calendars and ChatGPT using the old link stop updating. Calendars can take a few hours to pick up changes, so Classy shows them first.
              </p>
            </div>
          </details>

          {/* Web apps can't have home-screen widgets, but calendar apps can: theirs then shows the next class. */}
          <p className="text-[13px] leading-snug font-semibold text-muted">
            <span className="font-extrabold text-navy">Tip:</span> add your calendar’s widget to your home screen to see your next class at a glance.
          </p>
        </>
      )}
    </Sheet>
  )
}
