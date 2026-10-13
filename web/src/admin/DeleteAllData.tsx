import { useState } from 'react'
import { api, errorText } from '../lib/api'
import { Button, ErrorText, Modal } from '../components/ui'

const STEPS = [
  {
    title: 'Delete all data?',
    body: 'This deletes every semester with its courses, sections, students’ picks and defaults, every major, and every student who isn’t an owner. Dashboard accounts and owners stay.',
    label: 'Yes, continue',
  },
  { title: 'Are you sure?', body: 'Nothing can be brought back. Students will have to sign up and set up again.', label: 'Yes, I’m sure' },
  { title: 'Last chance.', body: 'Delete everything now?', label: 'Delete everything' },
]

/** Owners only: wipes the app's data after three confirmations. */
export default function DeleteAllData({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [done, setDone] = useState('')
  const s = step === null ? null : STEPS[step]
  const close = () => {
    setStep(null)
    setError('')
  }

  return (
    <section className="flex flex-col gap-2 border-t-[2.5px] border-alert pt-4">
      <h2 className="text-[24px] font-extrabold text-alert">Delete all data</h2>
      <p className="text-[15px] font-semibold text-muted">Starts the app over. Asks three times first.</p>
      <div>
        <Button variant="danger" size="sm" onClick={() => setStep(0)}>
          Delete all data
        </Button>
      </div>
      {done && <p className="text-[15px] font-bold">{done}</p>}
      <Modal open={step !== null} onClose={close} label={s?.title ?? 'Delete all data'}>
        {s && (
          <>
            <span className="label text-[11px] tracking-[0.12em] text-alert">Step {step! + 1} of 3</span>
            <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">{s.title}</h2>
            <p className="text-[16px] leading-snug font-semibold text-muted">{s.body}</p>
            <ErrorText>{error}</ErrorText>
            <div className="flex justify-end gap-2.5">
              <Button variant="secondary" onClick={close}>
                Cancel
              </Button>
              <Button
                className="bg-alert! text-cream!"
                busy={busy}
                onClick={async () => {
                  if (step! < STEPS.length - 1) return setStep(step! + 1)
                  setBusy(true)
                  setError('')
                  try {
                    const r = await api.post<{ semesters: number; students: number; majors: number }>('/admin/delete-all-data', { confirm: 'DELETE ALL DATA' })
                    setDone(`Deleted ${r.semesters} semesters, ${r.majors} majors and ${r.students} students.`)
                    close()
                    onDone()
                  } catch (e) {
                    setError(errorText(e))
                  } finally {
                    setBusy(false)
                  }
                }}
              >
                {s.label}
              </Button>
            </div>
          </>
        )}
      </Modal>
    </section>
  )
}
