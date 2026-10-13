/**
 * Active bindings over time, by day, ISO week or month, from the running total of /daily-changes.
 *
 * A population is a stock, not a flow: a week's value is the stock at the end of the week, not a sum
 * of its days. So each bucket takes the cumulative of its **last delivered day**. A bucket in which no
 * day was delivered has no value - a gap, never a zero and never a carried-forward figure, because
 * either would draw a point the data does not have.
 *
 * Weeks are ISO weeks, Monday to Sunday (the same `toMonday` the arrivals endpoint uses); months are
 * calendar months. Dates are business dates, stepped in UTC so no daylight-saving change moves one.
 */
import type { TrendGrain } from './config'

export interface TrendRow {
  date: string
  cumulative: number
}

export interface TrendBucket {
  /** The day itself, the week's Monday, or the month's first day. */
  key: string
  /** The cumulative on the last delivered day in the bucket; null when none was delivered. */
  value: number | null
  /** That day, or null. */
  lastDelivered: string | null
  /** Delivered days in the bucket. */
  delivered: number
  /** Flagged days among them, ascending. */
  flaggedDays: string[]
}

function utc(date: string): Date {
  return new Date(`${date}T00:00:00Z`)
}

function iso(d: Date): string {
  return d.toISOString().slice(0, 10)
}

/** Monday of the ISO week a date falls in. */
export function isoWeekStart(date: string): string {
  const d = utc(date)
  const sinceMonday = (d.getUTCDay() + 6) % 7
  d.setUTCDate(d.getUTCDate() - sinceMonday)
  return iso(d)
}

export function monthStart(date: string): string {
  return `${date.slice(0, 7)}-01`
}

/** The bucket a date belongs to. */
export function bucketKey(date: string, grain: TrendGrain): string {
  return grain === 'week' ? isoWeekStart(date) : grain === 'month' ? monthStart(date) : date
}

/** The bucket after this one. */
export function nextBucket(key: string, grain: TrendGrain): string {
  const d = utc(key)
  if (grain === 'month') d.setUTCMonth(d.getUTCMonth() + 1, 1)
  else d.setUTCDate(d.getUTCDate() + (grain === 'week' ? 7 : 1))
  return iso(d)
}

/** The last calendar day of a bucket. */
export function bucketEnd(key: string, grain: TrendGrain): string {
  const d = utc(nextBucket(key, grain))
  d.setUTCDate(d.getUTCDate() - 1)
  return iso(d)
}

/**
 * Every bucket from the first row's to the last row's, in order, each with its value or a gap.
 * Input order does not matter.
 */
export function bucketTrend(
  rows: readonly TrendRow[],
  grain: TrendGrain,
  flagged: ReadonlyMap<string, string> | ReadonlySet<string> = new Set<string>(),
): TrendBucket[] {
  if (rows.length === 0) return []
  const sorted = [...rows].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0))

  const byKey = new Map<string, TrendBucket>()
  for (const row of sorted) {
    const key = bucketKey(row.date, grain)
    let bucket = byKey.get(key)
    if (!bucket) {
      bucket = { key, value: null, lastDelivered: null, delivered: 0, flaggedDays: [] }
      byKey.set(key, bucket)
    }
    // Ascending, so the last row seen is the bucket's last delivered day.
    bucket.value = row.cumulative
    bucket.lastDelivered = row.date
    bucket.delivered++
    if (flagged.has(row.date)) bucket.flaggedDays.push(row.date)
  }

  const out: TrendBucket[] = []
  const last = bucketKey(sorted[sorted.length - 1]!.date, grain)
  for (let key = bucketKey(sorted[0]!.date, grain); key <= last; key = nextBucket(key, grain)) {
    out.push(byKey.get(key) ?? { key, value: null, lastDelivered: null, delivered: 0, flaggedDays: [] })
  }
  return out
}
