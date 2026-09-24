/**
 * Calendar days, for series that must show the days they do not have.
 *
 * The daily series come back with one row per delivered day and nothing for a day that was not
 * delivered. Drawn on a category axis as they are, a missing day simply is not there: the bars
 * close up and a line joins the day before to the day after - an interpolated point, drawn as if
 * it were data. Filling the calendar first gives each missing day its own slot with no value,
 * which ECharts draws as a break.
 *
 * Dates are `YYYY-MM-DD` business dates with no time or zone, and are stepped in UTC so no local
 * daylight-saving change can skip or repeat a day.
 */

const DAY_MS = 86_400_000

function toDay(iso: string): number {
  return Date.parse(`${iso}T00:00:00Z`) / DAY_MS
}

function fromDay(day: number): string {
  return new Date(day * DAY_MS).toISOString().slice(0, 10)
}

/** A calendar day with no row, standing in the series where it would have been. */
export interface MissingDay {
  date: string
  missing: true
}

/**
 * Every day from the first row to the last, rows in place and the days between them as
 * {@link MissingDay}. Input order does not matter; output is ascending.
 */
export function fillCalendar<T extends { date: string }>(rows: readonly T[]): Array<T | MissingDay> {
  if (rows.length === 0) return []

  const sorted = [...rows].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0))
  const out: Array<T | MissingDay> = []
  let expected = toDay(sorted[0]!.date)

  for (const row of sorted) {
    const day = toDay(row.date)
    for (; expected < day; expected++) out.push({ date: fromDay(expected), missing: true })
    out.push(row)
    expected = day + 1
  }
  return out
}

export function isMissing<T>(row: T | MissingDay): row is MissingDay {
  return (row as MissingDay).missing === true
}

/** A stretch of consecutive days. */
export interface DayRun {
  first: string
  last: string
  days: number
}

/** Dates grouped into runs of consecutive days, ascending. Duplicates count once. */
export function dayRuns(dates: readonly string[]): DayRun[] {
  const days = [...new Set(dates.map(toDay))].sort((a, b) => a - b)
  const runs: DayRun[] = []

  for (const day of days) {
    const last = runs[runs.length - 1]
    if (last && toDay(last.last) === day - 1) {
      last.last = fromDay(day)
      last.days++
    } else {
      runs.push({ first: fromDay(day), last: fromDay(day), days: 1 })
    }
  }
  return runs
}
