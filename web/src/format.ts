const IST = 'Asia/Kolkata'

export function formatDate(value: string | null) {
  return value ? new Date(value).toLocaleString('en-IN', { timeZone: IST, dateStyle: 'medium', timeStyle: 'short' }) : '—'
}

export function formatTime(value: string | null) {
  return value ? new Date(value).toLocaleTimeString('en-IN', { timeZone: IST, hour: 'numeric', minute: '2-digit' }) : '—'
}

/** Formats a yyyy-mm-dd clinic date, for example "Wed, 14 Oct". */
export function formatDay(date: string) {
  return new Date(`${date}T12:00:00+05:30`).toLocaleDateString('en-IN', { timeZone: IST, weekday: 'short', day: 'numeric', month: 'short' })
}

export function todayIst(): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: IST }).format(new Date())
}

export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T12:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}

export function estimateText(min: number | null, max: number | null) {
  if (min === null || max === null) return null
  if (max <= 5) return 'Any moment now'
  return `About ${min}–${max} min`
}

export function newIdempotencyKey(): string {
  return typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

/** Returns a form-level `onInput` handler that clears a field's server error as soon as the user edits it. */
export function clearFieldErrorOnInput(setErrors: (update: (prev: Record<string, string[]>) => Record<string, string[]>) => void) {
  return (event: { target: EventTarget }) => {
    const name = (event.target as HTMLInputElement).name
    if (!name) return
    setErrors((prev) => {
      if (!prev[name]) return prev
      const next = { ...prev }
      delete next[name]
      return next
    })
  }
}
