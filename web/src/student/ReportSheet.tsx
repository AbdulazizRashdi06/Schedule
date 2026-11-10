import { useState } from 'react'
import { useLocation } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Sheet } from '../components/ui'

/** "Report a problem": a short note that lands in the dashboard's Feedback page. */
export default function ReportSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { pathname } = useLocation()
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [sent, setSent] = useState(false)

  const close = () => {
    onClose()
    if (sent) {
      setSent(false)
      setText('')
    }
  }

  const send = async () => {
    setBusy(true)
    setError('')
    try {
      await api.post('/me/reports', { text, page: pathname })
      setSent(true)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Sheet open={open} onClose={close} label="Report a problem">
      {sent ? (
        <>
          <h2 className="pt-2 text-[30px] leading-none font-extrabold tracking-[-0.03em]">Thanks, got it.</h2>
          <p className="text-[16px] font-semibold text-muted">We read every report and fix what we can.</p>
          <Button className="mt-2" onClick={close}>
            Done
          </Button>
        </>
      ) : (
        <>
          <h2 className="pt-2 text-[30px] leading-none font-extrabold tracking-[-0.03em]">Report a problem</h2>
          <p className="text-[15px] font-semibold text-muted">Wrong class, wrong room, something broken? Tell us what happened.</p>
          <label htmlFor="report-text" className="sr">
            What went wrong
          </label>
          <textarea
            id="report-text"
            value={text}
            maxLength={1000}
            rows={5}
            placeholder="e.g. CS1107 on Monday shows room B204 but it's in A101"
            onChange={(e) => setText(e.target.value)}
            className="rounded-[14px] bg-paper p-3 text-[16px] font-semibold text-navy outline-none placeholder:text-navy/45"
          />
          <ErrorText>{error}</ErrorText>
          <Button busy={busy} disabled={!text.trim()} onClick={send}>
            Send report
          </Button>
        </>
      )}
    </Sheet>
  )
}
