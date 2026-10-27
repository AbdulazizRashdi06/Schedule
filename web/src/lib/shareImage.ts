import { DAY_LONG, DAY_SHORT, minutes } from './time'
import { SLOTS, TYPE_NAME, addDays, fmtDate, type ClassType, type Session } from './slots'

export type ShareFormat = 'week' | 'story'

/** What to draw: the week grid, or one day's list when `day` is set. */
export interface ShareInput {
  sessions: Session[]
  days: number[]
  start: Date
  title: string
  range: string
  day?: number
}

const SIZES: Record<ShareFormat, [number, number]> = { week: [1080, 1080], story: [1080, 1920] }
const DISPLAY = '"Bricolage Grotesque", system-ui, sans-serif'
const MONO = '"JetBrains Mono", ui-monospace, monospace'

/** The page's own colours, so the picture follows the student's theme. */
function palette() {
  const css = getComputedStyle(document.documentElement)
  const v = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback
  return {
    navy: v('--color-navy', '#0b1957'),
    navy800: v('--color-navy-800', '#13226a'),
    line: v('--color-navy-line', '#2c3d85'),
    cream: v('--color-cream', '#f8f3ea'),
    sky: v('--color-sky', '#9eccfa'),
    ice: v('--color-ice', '#d1e8ff'),
    paper: v('--color-paper', '#fffdf8'),
  }
}
type Palette = ReturnType<typeof palette>

const blockFill = (p: Palette, t: ClassType) => ({ lecture: p.cream, lab: p.ice, tutorial: p.sky, mine: p.paper })[t]

function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
  ctx.beginPath()
  ctx.roundRect(x, y, w, h, Math.min(r, w / 2, h / 2))
}

/** Text cut with "…" to fit `max` pixels. */
function fit(ctx: CanvasRenderingContext2D, text: string, max: number) {
  if (ctx.measureText(text).width <= max) return text
  let t = text
  while (t.length > 1 && ctx.measureText(t + '…').width > max) t = t.slice(0, -1)
  return t + '…'
}

function header(ctx: CanvasRenderingContext2D, p: Palette, w: number, input: ShareInput, scale: number) {
  const pad = 72
  ctx.fillStyle = p.ice
  ctx.font = `700 ${26 * scale}px ${MONO}`
  ctx.letterSpacing = '4px'
  ctx.fillText(input.day === undefined ? 'MY WEEK' : 'MY DAY', pad, pad + 26 * scale)
  ctx.letterSpacing = '0px'
  ctx.fillStyle = p.sky
  ctx.font = `800 ${104 * scale}px ${DISPLAY}`
  const title = input.day === undefined ? input.title : DAY_LONG[input.day]
  ctx.fillText(fit(ctx, title, w - pad * 2), pad, pad + 26 * scale + 112 * scale)
  ctx.fillStyle = p.ice
  ctx.font = `700 ${28 * scale}px ${MONO}`
  const sub = input.day === undefined ? input.range.toUpperCase() : `${fmtDate(addDays(input.start, input.day)).toUpperCase()} · ${input.title.toUpperCase()}`
  ctx.fillText(sub, pad, pad + 26 * scale + 112 * scale + 52 * scale)
  return pad + 26 * scale + 112 * scale + 52 * scale + 48 * scale
}

function footer(ctx: CanvasRenderingContext2D, p: Palette, w: number, h: number) {
  const pad = 72
  ctx.strokeStyle = p.line
  ctx.lineWidth = 3
  ctx.beginPath()
  ctx.moveTo(pad, h - pad - 58)
  ctx.lineTo(w - pad, h - pad - 58)
  ctx.stroke()
  ctx.fillStyle = p.cream
  ctx.font = `800 44px ${DISPLAY}`
  ctx.fillText('Classy.', pad, h - pad)
  ctx.fillStyle = p.ice
  ctx.font = `700 24px ${MONO}`
  ctx.textAlign = 'right'
  ctx.fillText('SCHEDULE.TRYZARQA.COM', w - pad, h - pad - 6)
  ctx.textAlign = 'left'
  return h - pad - 58
}

function block(ctx: CanvasRenderingContext2D, p: Palette, s: Session, x: number, y: number, w: number, h: number, big: boolean) {
  roundRect(ctx, x, y, w, h, big ? 26 : 18)
  ctx.fillStyle = blockFill(p, s.type)
  ctx.fill()
  if (s.mine) {
    ctx.setLineDash([10, 8])
    ctx.strokeStyle = p.navy
    ctx.globalAlpha = 0.5
    ctx.lineWidth = 3
    ctx.stroke()
    ctx.globalAlpha = 1
    ctx.setLineDash([])
  }
  const inset = big ? 20 : 14
  const max = w - inset * 2
  ctx.fillStyle = p.navy
  let ty = y + inset + (big ? 30 : 24)
  ctx.font = `800 ${big ? 32 : 24}px ${DISPLAY}`
  ctx.fillText(fit(ctx, s.mine ? s.cls.name : s.cls.code, max), x + inset, ty)
  // The row already says when it starts, so the room comes first where space is short.
  const time = `${s.meeting.start}–${s.meeting.end}`
  const room = s.meeting.room || (s.mine ? '' : 'Room TBA')
  const lines = big ? [s.mine ? '' : s.cls.name, time, room] : [room, time]
  ctx.font = `700 ${big ? 22 : 17}px ${MONO}`
  for (const line of lines.filter(Boolean)) {
    ty += big ? 34 : 26
    if (ty > y + h - inset + 4) break
    ctx.fillText(fit(ctx, line, max), x + inset, ty)
  }
}

/** The week grid: day columns, one row per teaching slot, classes spanning the slots they cover. */
function drawWeek(ctx: CanvasRenderingContext2D, p: Palette, w: number, top: number, bottom: number, input: ShareInput, story: boolean) {
  const pad = 72
  const timeCol = 84
  const gap = 10
  const days = input.days
  const colW = (w - pad * 2 - timeCol - gap * (days.length - 1)) / days.length
  const dayRow = 52
  const rowH = (bottom - top - 40 - dayRow - gap * (SLOTS.length - 1)) / SLOTS.length
  const gridTop = top + dayRow

  ctx.font = `700 22px ${MONO}`
  ctx.fillStyle = p.ice
  days.forEach((d, i) => {
    const date = addDays(input.start, d)
    ctx.fillText(`${DAY_SHORT[d].toUpperCase()} ${date.getDate()}`, pad + timeCol + i * (colW + gap) + 4, top + 30)
  })
  SLOTS.forEach(([s], r) => {
    const y = gridTop + r * (rowH + gap)
    ctx.fillStyle = p.ice
    ctx.font = `700 20px ${MONO}`
    ctx.fillText(s, pad, y + 26)
    days.forEach((_, i) => {
      roundRect(ctx, pad + timeCol + i * (colW + gap), y, colW, rowH, 16)
      ctx.fillStyle = p.navy800
      ctx.fill()
    })
  })

  // Classes that share a start slot (clashes) sit side by side.
  days.forEach((d, i) => {
    const today = input.sessions.filter((s) => s.meeting.day === d)
    const bySlot = new Map<number, Session[]>()
    for (const s of today) bySlot.set(s.slot, [...(bySlot.get(s.slot) ?? []), s])
    for (const [slot, group] of bySlot) {
      group.forEach((s, k) => {
        const x = pad + timeCol + i * (colW + gap) + (k * colW) / group.length
        const y = gridTop + slot * (rowH + gap)
        const h = s.span * rowH + (s.span - 1) * gap
        block(ctx, p, s, x + (k > 0 ? 3 : 0), y, colW / group.length - (group.length > 1 ? 3 : 0), h, story && group.length === 1 && colW > 200)
      })
    }
  })
  if (input.sessions.length === 0) {
    ctx.fillStyle = p.sky
    ctx.font = `800 64px ${DISPLAY}`
    ctx.fillText('Free week.', pad, gridTop + rowH)
  }
}

/** One day as a list: big start time, then the class and its room. */
function drawDay(ctx: CanvasRenderingContext2D, p: Palette, w: number, top: number, bottom: number, input: ShareInput) {
  const pad = 72
  const list = input.sessions.filter((s) => s.meeting.day === input.day).sort((a, b) => minutes(a.meeting.start) - minutes(b.meeting.start))
  if (list.length === 0) {
    ctx.fillStyle = p.sky
    ctx.font = `800 88px ${DISPLAY}`
    ctx.fillText('Free day.', pad, top + 100)
    return
  }
  const rowH = Math.min(190, (bottom - top - 20) / list.length)
  list.forEach((s, i) => {
    const y = top + i * rowH
    ctx.strokeStyle = p.line
    ctx.lineWidth = 3
    ctx.beginPath()
    ctx.moveTo(pad, y)
    ctx.lineTo(w - pad, y)
    ctx.stroke()
    ctx.fillStyle = p.sky
    ctx.font = `800 ${Math.min(76, rowH * 0.5)}px ${DISPLAY}`
    ctx.fillText(s.meeting.start, pad, y + rowH * 0.58)
    ctx.fillStyle = p.cream
    ctx.font = `800 ${Math.min(38, rowH * 0.24)}px ${DISPLAY}`
    ctx.fillText(fit(ctx, s.cls.name, w - pad * 2 - 280), pad + 270, y + rowH * 0.45)
    ctx.fillStyle = p.ice
    ctx.font = `700 ${Math.min(22, rowH * 0.15)}px ${MONO}`
    const meta = `${TYPE_NAME[s.type]}, ${s.meeting.room || 'Room TBA'} · until ${s.meeting.end}`.toUpperCase()
    ctx.fillText(fit(ctx, meta, w - pad * 2 - 280), pad + 270, y + rowH * 0.45 + 40)
  })
}

/** Draws the timetable picture in the app's colours and returns it as a PNG. */
export async function renderShare(input: ShareInput, format: ShareFormat): Promise<Blob> {
  await Promise.all([document.fonts.load(`800 40px ${DISPLAY}`), document.fonts.load(`700 20px ${MONO}`)]).catch(() => {})
  const [w, h] = SIZES[format]
  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  const ctx = canvas.getContext('2d')!
  const p = palette()
  ctx.fillStyle = p.navy
  ctx.fillRect(0, 0, w, h)
  ctx.textBaseline = 'alphabetic'
  const scale = format === 'story' ? 1.15 : 1
  const top = header(ctx, p, w, input, scale)
  const bottom = footer(ctx, p, w, h)
  if (input.day === undefined) drawWeek(ctx, p, w, top, bottom, input, format === 'story')
  else drawDay(ctx, p, w, top + 20, bottom - 30, input)
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('Could not make the picture.'))), 'image/png'))
}
