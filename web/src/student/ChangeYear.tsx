import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorText } from '../lib/api'
import { yearName } from '../lib/year'
import { Button, ErrorText, Modal, cx } from '../components/ui'

const YEARS = [0, 1, 2, 3, 4, 5]

/** Change study year after setup (postponed a year, or picked the wrong one). Core courses switch to the new year's. */
export default function ChangeYear({ open, onClose, current }: { open: boolean; onClose: () => void; current: number | null }) {
  const navigate = useNavigate()
  const [year, setYear] = useState<number | null>(current)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const changed = year !== null && year !== current

  return (
    <Modal open={open} onClose={onClose} label="Change my year">
      <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">Change my year</h2>
      <div role="radiogroup" aria-label="Study year" className="grid grid-cols-3 gap-2">
        {YEARS.map((y) => (
          <button
            key={y}
            type="button"
            role="radio"
            aria-checked={year === y}
            onClick={() => setYear(y)}
            className={cx('press h-14 rounded-[16px] text-[17px] font-extrabold', year === y ? 'bg-navy text-cream' : 'shadow-[inset_0_0_0_2px_rgb(11_25_87/0.16)]')}
          >
            {yearName(y)}
          </button>
        ))}
      </div>
      <p className="text-[15px] leading-snug font-semibold text-muted">
        {changed
          ? `Your core courses switch to ${yearName(year)}’s. Life skills and electives stay. You can check sections next.`
          : current === null
            ? 'Pick your year.'
            : `You’re in ${yearName(current)}.`}
      </p>
      <ErrorText>{error}</ErrorText>
      <div className="flex justify-end gap-2.5">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          busy={busy}
          disabled={!changed}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await api.put('/me/profile', { year })
              onClose()
              navigate('/edit')
            } catch (e) {
              setError(errorText(e))
            } finally {
              setBusy(false)
            }
          }}
        >
          {busy ? 'Saving…' : 'Change year'}
        </Button>
      </div>
    </Modal>
  )
}
