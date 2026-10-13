import { useCallback, useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type ComponentProps, type CSSProperties, type ReactNode } from 'react'

const cx = (...c: (string | false | null | undefined)[]) => c.filter(Boolean).join(' ')
export { cx }

/** Lucide-style stroke icons. */
const PATHS = {
  back: 'M15 18l-6-6 6-6',
  prev: 'M19 12H5M11 18l-6-6 6-6',
  next: 'M5 12h14M13 6l6 6-6 6',
  arrow: 'M5 12h14M13 6l6 6-6 6',
  out: 'M7 17L17 7M8 7h9v9',
  menu: 'M4 7h16M4 12h16M4 17h10',
  pin: 'M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0116 0zM12 13a3 3 0 100-6 3 3 0 000 6z',
  bell: 'M6 8a6 6 0 0112 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.94 1.94 0 003.4 0',
  check: 'M20 6L9 17l-5-5',
  x: 'M6 6l12 12M18 6L6 18',
  alert: 'M12 22a10 10 0 100-20 10 10 0 000 20zM12 8v4M12 16h.01',
  clock: 'M12 22a10 10 0 100-20 10 10 0 000 20zM12 6v6l4 2',
  warn: 'M10.3 3.9L1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0zM12 9v4M12 17h.01',
  redo: 'M3 12a9 9 0 103-6.7L3 8M3 3v5h5',
} as const
export type IconName = keyof typeof PATHS

export function Icon({ name, className, strokeWidth = 2.4 }: { name: IconName; className?: string; strokeWidth?: number }) {
  return (
    <svg viewBox="0 0 24 24" className={cx('size-5 shrink-0', className)} fill="none" stroke="currentColor" strokeWidth={strokeWidth} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={PATHS[name]} />
    </svg>
  )
}

/**
 * Keeps a dialog mounted while it animates out. `shown` flips a frame after mounting so the
 * enter transition runs, and back before unmounting so the exit one does.
 */
export function usePresence(open: boolean, ms = 340) {
  const [mounted, setMounted] = useState(open)
  const [shown, setShown] = useState(false)
  useEffect(() => {
    if (open) {
      setMounted(true)
      const f = requestAnimationFrame(() => requestAnimationFrame(() => setShown(true)))
      return () => cancelAnimationFrame(f)
    }
    setShown(false)
    const t = setTimeout(() => setMounted(false), ms)
    return () => clearTimeout(t)
  }, [open, ms])
  return { mounted, shown }
}

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'sky' | 'cream'
type Size = 'lg' | 'md' | 'sm'

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-navy text-cream',
  secondary: 'bg-transparent text-navy shadow-[inset_0_0_0_2px_rgb(11_25_87/0.25)]',
  ghost: 'bg-transparent text-navy tint',
  danger: 'bg-transparent text-alert tint',
  sky: 'bg-sky text-navy',
  cream: 'bg-cream text-navy',
}
const SIZES: Record<Size, string> = {
  lg: 'h-[60px] px-6 text-[18px]',
  md: 'h-12 px-[22px] text-[16px]',
  sm: 'h-10 px-4 text-[14px]',
}

/** Pill button. Disabled looks muted but keeps its label so people know what's blocking. */
export function Button({ variant = 'primary', size = 'md', className, busy, children, ...rest }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: Size; busy?: boolean }) {
  const off = busy || rest.disabled
  return (
    <button
      {...rest}
      disabled={off}
      aria-busy={busy || undefined}
      className={cx(
        'press inline-flex items-center justify-center gap-2.5 rounded-full font-extrabold whitespace-nowrap',
        SIZES[size],
        rest.disabled && !busy ? 'bg-navy/10 text-navy/60 shadow-none' : VARIANTS[variant],
        className,
      )}
    >
      {children}
    </button>
  )
}

/** Mono, uppercase, tracked: labels, times, IDs. */
export function Mono({ children, className, id }: { children: ReactNode; className?: string; id?: string }) {
  return (
    <span id={id} className={cx('label text-[12px] tracking-[0.12em]', className)}>
      {children}
    </span>
  )
}

/** Label above, paper field below. Pass `invalid` to mark the field and announce it. */
export function TextField({
  label,
  invalid,
  trailing,
  mono,
  className,
  boxClassName,
  hideLabel,
  ...rest
}: ComponentProps<'input'> & { label: ReactNode; invalid?: boolean; trailing?: ReactNode; mono?: boolean; boxClassName?: string; hideLabel?: boolean }) {
  const auto = useId()
  const id = rest.id ?? auto
  return (
    <div className={cx('flex flex-col gap-1.5', className)}>
      <label htmlFor={id} className={cx('label text-[11px] tracking-[0.12em]', hideLabel && 'sr')}>
        {label}
      </label>
      <div data-invalid={invalid || undefined} className={cx('field flex h-12 items-center gap-2 rounded-[14px] bg-paper px-3.5', boxClassName)}>
        <input
          {...rest}
          id={id}
          aria-invalid={invalid || undefined}
          className={cx('min-w-0 flex-1 bg-transparent text-[16px] font-semibold text-navy outline-none placeholder:text-[#9AA3C7]', mono && 'font-mono font-bold')}
        />
        {trailing}
      </div>
    </div>
  )
}

export function Select({ className, children, ...rest }: ComponentProps<'select'>) {
  return (
    <select {...rest} className={cx('field h-12 rounded-[14px] bg-paper px-3 text-[16px] font-bold text-navy outline-none', className)}>
      {children}
    </select>
  )
}

/** Navy tick box (role=checkbox). */
export function Checkbox({ checked, onChange, label, size = 'md' }: { checked: boolean; onChange: (v: boolean) => void; label?: string; size?: 'md' | 'lg' }) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className={cx(
        'press grid shrink-0 place-items-center',
        size === 'lg' ? 'size-8 rounded-[10px]' : 'size-[26px] rounded-[8px]',
        checked ? 'bg-navy text-cream' : 'border-2 border-navy/35',
      )}
    >
      {checked && <Icon name="check" className="pop size-4" strokeWidth={3.4} />}
    </button>
  )
}

/** Small spinner, only for "Checking…" style waits. Page loads use skeletons. */
export function Spinner({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" className={cx('spin size-[18px]', className)} fill="none" stroke="currentColor" strokeWidth={3} strokeLinecap="round" aria-hidden="true">
      <path d="M21 12a9 9 0 11-6.2-8.56" />
    </svg>
  )
}

/** A grey placeholder shape with a shimmer. */
export function Sk({ className, style, dark }: { className?: string; style?: CSSProperties; dark?: boolean }) {
  return <span className={cx('sk', dark && 'on-navy', className)} style={style} aria-hidden="true" />
}

/** Placeholder rows for a list or table that's loading. */
export function Loading({ rows = 4, label = 'Loading' }: { rows?: number; label?: string }) {
  return (
    <div aria-busy="true" aria-label={label} className="flex flex-col gap-4 py-4">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="flex items-center gap-3">
          <Sk className="h-[30px] w-[84px] rounded-lg" />
          <span className="flex flex-1 flex-col gap-1.5">
            <Sk className="h-4 rounded-md" style={{ width: `${70 - i * 8}%` }} />
            <Sk className="h-3 rounded-md" style={{ width: `${40 - i * 4}%` }} />
          </span>
        </div>
      ))}
    </div>
  )
}

/** Error text on cream, announced. */
export function ErrorText({ children, className }: { children?: ReactNode; className?: string }) {
  if (!children) return null
  return (
    <p role="alert" className={cx('msg flex items-start gap-2 text-[15px] font-semibold text-alert', className)}>
      <Icon name="alert" className="mt-px size-[18px]" />
      <span>{children}</span>
    </p>
  )
}

/** Clash and error notices are alert pink; info notices are ice. */
export function Notice({ children, tone = 'info', className }: { children: ReactNode; tone?: 'info' | 'warn'; className?: string }) {
  return (
    <div
      role={tone === 'warn' ? 'alert' : undefined}
      className={cx('open flex items-start gap-2 rounded-[12px] px-3 py-2.5 text-[14px] leading-snug font-bold', tone === 'warn' ? 'bg-alert-bg text-alert-ink' : 'bg-ice text-navy', className)}
    >
      {tone === 'warn' && <Icon name="warn" className="mt-px size-4" strokeWidth={2.6} />}
      <span className="min-w-0">{children}</span>
    </div>
  )
}

/** Mono pill: CORE, CHANGED, NEEDS FIX… */
export function Tag({ children, tone = 'ice', className }: { children: ReactNode; tone?: 'ice' | 'navy' | 'alert' | 'plain' | 'sky'; className?: string }) {
  const tones = { ice: 'bg-ice text-navy', navy: 'bg-navy text-cream', alert: 'bg-alert-bg text-alert-ink', plain: 'text-muted', sky: 'bg-sky text-navy' }
  return <span className={cx('label inline-flex shrink-0 items-center rounded-full px-2 py-1 text-[10px] tracking-[0.1em]', tones[tone], className)}>{children}</span>
}

/** Escape closes, the page behind can't scroll, focus moves in and comes back. */
function useDialog(open: boolean, onClose: () => void) {
  const ref = useRef<HTMLElement>(null)
  const close = useRef(onClose)
  useEffect(() => {
    close.current = onClose
  })
  useEffect(() => {
    if (!open) return
    const back = document.activeElement as HTMLElement | null
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close.current()
    document.addEventListener('keydown', onKey)
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    requestAnimationFrame(() => ref.current?.focus())
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = prev
      back?.focus?.()
    }
  }, [open])
  return ref
}

/** Bottom sheet: slides up on the drawer curve, scrim fades. Tapping the scrim closes it. */
export function Sheet({ open, onClose, label, children, className }: { open: boolean; onClose: () => void; label: string; children: ReactNode; className?: string }) {
  const { mounted, shown } = usePresence(open)
  const ref = useDialog(open, onClose)
  if (!mounted) return null
  return (
    <div className="fixed inset-0 z-50">
      <div className="scrim absolute inset-0 bg-navy/45" data-shown={shown} onClick={onClose} />
      <section
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        data-shown={shown}
        className={cx(
          'sheet absolute inset-x-0 bottom-0 mx-auto flex max-h-[88dvh] w-full max-w-[480px] flex-col gap-2.5 overflow-y-auto rounded-t-[32px] bg-cream px-6 pt-3 pb-[max(1.75rem,env(safe-area-inset-bottom))] shadow-[0_-20px_40px_-20px_rgb(11_25_87/0.5)] outline-none',
          className,
        )}
      >
        <span aria-hidden="true" className="h-[5px] w-10 shrink-0 self-center rounded-[3px] bg-navy/20" />
        {children}
      </section>
    </div>
  )
}

/** Dashboard side panel from the right. */
export function Drawer({ open, onClose, label, children }: { open: boolean; onClose: () => void; label: string; children: ReactNode }) {
  const { mounted, shown } = usePresence(open, 300)
  const ref = useDialog(open, onClose)
  if (!mounted) return null
  return (
    <div className="fixed inset-0 z-50">
      <div className="scrim absolute inset-0 bg-navy/35" data-shown={shown} onClick={onClose} />
      <section
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        data-shown={shown}
        className="drawer absolute inset-y-0 right-0 flex w-[420px] max-w-full flex-col gap-[18px] overflow-y-auto bg-cream p-8 shadow-[-24px_0_48px_-24px_rgb(11_25_87/0.5)] outline-none"
      >
        {children}
      </section>
    </div>
  )
}

/** Centred pop-up: scale 0.96 → 1 plus fade. */
export function Modal({ open, onClose, label, children }: { open: boolean; onClose: () => void; label: string; children: ReactNode }) {
  const { mounted, shown } = usePresence(open, 200)
  const ref = useDialog(open, onClose)
  if (!mounted) return null
  return (
    <div className="fixed inset-0 z-50 grid place-items-center p-4">
      <div className="scrim absolute inset-0 bg-navy/35" data-shown={shown} onClick={onClose} />
      <section
        ref={ref}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        data-shown={shown}
        className="modal relative flex max-h-[90dvh] w-[460px] max-w-full flex-col gap-[18px] overflow-y-auto rounded-[28px] bg-cream p-7 shadow-[0_32px_64px_-24px_rgb(11_25_87/0.55)] outline-none"
      >
        {children}
      </section>
    </div>
  )
}

/** A message that slides up from the bottom and leaves the same way. */
export function useToast(ms = 2600) {
  const [text, setText] = useState<string | null>(null)
  const [shown, setShown] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  const show = useCallback(
    (t: string) => {
      setText(t)
      setShown(true)
      clearTimeout(timer.current)
      timer.current = setTimeout(() => setShown(false), ms)
    },
    [ms],
  )
  useEffect(() => () => clearTimeout(timer.current), [])
  return { text, shown, show }
}

export function Toast({ toast, className }: { toast: { text: string | null; shown: boolean }; className?: string }) {
  return (
    <div role="status" aria-live="polite" data-shown={toast.shown} className={cx('toast fixed z-40', className)}>
      {toast.text}
    </div>
  )
}

export function Confirm({ open, title, body, confirmLabel, danger, onConfirm, onClose }: { open: boolean; title: string; body: ReactNode; confirmLabel: string; danger?: boolean; onConfirm: () => Promise<void> | void; onClose: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return (
    <Modal open={open} onClose={onClose} label={title}>
      <h2 className="text-[30px] leading-none font-extrabold tracking-[-0.03em]">{title}</h2>
      <div className="text-[16px] leading-snug font-semibold text-muted">{body}</div>
      <ErrorText>{error}</ErrorText>
      <div className="flex justify-end gap-2.5">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          className={danger ? 'bg-alert! text-cream!' : undefined}
          busy={busy}
          onClick={async () => {
            setBusy(true)
            setError('')
            try {
              await onConfirm()
              onClose()
            } catch (e) {
              setError(e instanceof Error ? e.message : 'Something went wrong.')
            } finally {
              setBusy(false)
            }
          }}
        >
          {busy ? 'Working…' : confirmLabel}
        </Button>
      </div>
    </Modal>
  )
}

/** Alternating class so the same error can shake the field again. */
export function useShake() {
  const [n, setN] = useState(0)
  return { shakeClass: n === 0 ? '' : n % 2 ? 'shake-a' : 'shake-b', shake: () => setN((x) => x + 1) }
}
