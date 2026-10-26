/**
 * Daily series against a dashboard's date range.
 *
 * The daily endpoints return every delivered day; a widget draws the days its effective range
 * covers. A day the source never delivered has no row - it is a gap, never a zero (lib/calendar.ts
 * draws it as one).
 */
import { fillCalendar, isMissing } from '@/lib/calendar'

/** A resolved range: null bounds are open (`all`, or a relative range not yet resolvable). */
export interface DateWindow {
  from: string | null
  to: string | null
}

/** The latest date among the rows, or null when there are none. Input order does not matter. */
export function lastDate(rows: readonly { date: string }[]): string | null {
  let last: string | null = null
  for (const row of rows) if (last === null || row.date > last) last = row.date
  return last
}

/** The rows dated inside the window, ascending. */
export function cutToWindow<T extends { date: string }>(rows: readonly T[], window: DateWindow): T[] {
  return rows
    .filter((r) => (window.from === null || r.date >= window.from) && (window.to === null || r.date <= window.to))
    .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0))
}

/** Dates inside the window, ascending and without duplicates. */
export function datesInWindow(dates: readonly string[], window: DateWindow): string[] {
  return [...new Set(dates)]
    .filter((d) => (window.from === null || d >= window.from) && (window.to === null || d <= window.to))
    .sort()
}

/**
 * The days missing between the first and last row: what a reader without the import calendar
 * (import.view) can still be told, because the series itself has the holes.
 */
export function seriesGaps(rows: readonly { date: string }[]): string[] {
  return fillCalendar(rows)
    .filter(isMissing)
    .map((d) => d.date)
}

const DAY_MS = 86_400_000

/** Calendar days from `from` to `to`, both included. */
export function daysInclusive(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / DAY_MS) + 1
}
