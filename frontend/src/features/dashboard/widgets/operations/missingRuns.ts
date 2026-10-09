/**
 * Missing days as the runs they form - "May 8–10, May 13, May 16–18" - not as first and last.
 *
 * The old freshness card printed the first and the last alone, so seven missing days read
 * "May 8 - May 18", which anyone takes to mean eleven. Moved here unchanged when that card became the
 * freshness widget.
 */
import { dayRuns, type DayRun } from '@/lib/calendar'
import { formatDate } from '@/lib/format'

const shortDay = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })
const dayOnly = new Intl.DateTimeFormat('en-US', { day: 'numeric', timeZone: 'UTC' })

/** "May 13", "May 8–10", "Apr 30 – May 2"; with the year on each end when runs span years. */
export function formatRun(run: DayRun, withYear: boolean): string {
  if (withYear) return run.first === run.last ? formatDate(run.first) : `${formatDate(run.first)} – ${formatDate(run.last)}`
  const first = new Date(`${run.first}T00:00:00Z`)
  const last = new Date(`${run.last}T00:00:00Z`)
  if (run.first === run.last) return shortDay.format(first)
  if (run.first.slice(0, 7) === run.last.slice(0, 7)) return `${shortDay.format(first)}–${dayOnly.format(last)}`
  return `${shortDay.format(first)} – ${shortDay.format(last)}`
}

export interface MissingSummary {
  /** Distinct missing days. */
  count: number
  labels: string[]
  /** The one year every run falls in, said once at the end; null when they span years. */
  year: string | null
}

export function summariseMissing(missing: readonly string[]): MissingSummary | null {
  if (!missing.length) return null
  const runs = dayRuns(missing)
  const years = new Set(runs.flatMap((r) => [r.first.slice(0, 4), r.last.slice(0, 4)]))
  const year = years.size === 1 ? [...years][0]! : null
  return { count: new Set(missing).size, labels: runs.map((r) => formatRun(r, year === null)), year }
}
