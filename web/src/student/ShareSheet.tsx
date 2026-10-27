import { useEffect, useState } from 'react'
import { canShareImages, saveImage, shareImage } from '../lib/native'
import { renderShare, type ShareFormat, type ShareInput } from '../lib/shareImage'
import { Button, ErrorText, Sheet, Spinner, cx } from '../components/ui'

const FORMATS: [ShareFormat, string, string][] = [
  ['week', 'Square', 'For chats'],
  ['story', 'Story', 'For status and stories'],
]

/** Share the week (or the day on screen) as a picture in the student's colours. Nothing leaves the phone until they share it. */
export default function ShareSheet({ open, onClose, input }: { open: boolean; onClose: () => void; input: ShareInput }) {
  const [format, setFormat] = useState<ShareFormat>('week')
  const [blob, setBlob] = useState<Blob | null>(null)
  const [url, setUrl] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const what = input.day === undefined ? 'week' : 'day'
  const key = `${format}|${input.day}|${input.start.getTime()}|${input.sessions.length}`

  useEffect(() => {
    if (!open) return
    let cancelled = false
    let made: string | null = null
    setBlob(null)
    setError('')
    renderShare(input, format).then(
      (b) => {
        if (cancelled) return
        made = URL.createObjectURL(b)
        setBlob(b)
        setUrl(made)
      },
      () => !cancelled && setError('Couldn’t make the picture. Try again.'),
    )
    return () => {
      cancelled = true
      if (made) URL.revokeObjectURL(made)
    }
    // Redraw when what's shown changes, not on every render of the parent.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, key])

  const name = `classy-${what}-${format}.png`
  const share = async () => {
    if (!blob) return
    setBusy(true)
    setError('')
    try {
      await shareImage(blob, name, `My ${what} on Classy`)
    } catch {
      setError('Sharing didn’t work here. Save the picture instead.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Sheet open={open} onClose={onClose} label={`Share my ${what}`}>
      <h2 className="display text-[34px] leading-none tracking-[-0.04em] text-navy">Share my {what}.</h2>
      <div role="radiogroup" aria-label="Picture size" className="grid grid-cols-2 gap-2">
        {FORMATS.map(([id, label, hint]) => (
          <button
            key={id}
            type="button"
            role="radio"
            aria-checked={format === id}
            onClick={() => setFormat(id)}
            className={cx('press flex flex-col items-start rounded-[16px] px-4 py-2.5 text-left', format === id ? 'bg-navy text-cream' : 'text-navy shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
          >
            <span className="text-[16px] font-extrabold">{label}</span>
            <span className={cx('text-[12px] font-semibold', format === id ? 'text-ice' : 'text-muted')}>{hint}</span>
          </button>
        ))}
      </div>
      <div className={cx('grid place-items-center overflow-hidden rounded-[18px] bg-navy', format === 'story' ? 'mx-auto aspect-[9/16] h-[46dvh]' : 'aspect-square w-full')}>
        {blob && url ? <img src={url} alt={`Preview of your ${what} as a picture`} className="size-full object-contain" /> : <Spinner className="text-sky" />}
      </div>
      <ErrorText>{error}</ErrorText>
      <div className="flex gap-2.5">
        {canShareImages() && (
          <Button className="flex-1" busy={busy} disabled={!blob} onClick={share}>
            Share
          </Button>
        )}
        <Button variant="secondary" className="flex-1" disabled={!blob} onClick={() => blob && saveImage(blob, name)}>
          Save picture
        </Button>
      </div>
    </Sheet>
  )
}
