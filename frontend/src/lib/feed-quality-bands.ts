/**
 * Shaded bands over the days the feed itself looked wrong.
 *
 * A day's SIM and handset changes are only as good as the file they came from. From 2026-07-27
 * SIMs began carrying several numbers a day and from 2026-09-15 a quarter of IMEIs arrived shifted
 * by a digit; both inflate the daily series, and a reader of the chart has no other way to know.
 * The band says "this stretch is suspect" where the numbers are, rather than in a footnote.
 *
 * Consecutive flagged days become one band, so a month-long defect is one shape and not thirty.
 */

import { chartColor } from '@/lib/chart-colors'

/** ECharts markArea for the flagged dates among `dates`, which must be ascending and contiguous. */
export function feedQualityMarkArea(dates: readonly string[], flagged: ReadonlyMap<string, string>) {
  const bands: Array<[{ xAxis: string }, { xAxis: string }]> = []
  let start: string | null = null
  let previous: string | null = null

  for (const date of dates) {
    if (flagged.has(date)) {
      start ??= date
      previous = date
    } else if (start !== null && previous !== null) {
      bands.push([{ xAxis: start }, { xAxis: previous }])
      start = null
    }
  }
  if (start !== null && previous !== null) bands.push([{ xAxis: start }, { xAxis: previous }])

  return {
    silent: true,
    itemStyle: { color: chartColor('--c-warning'), opacity: 0.14 },
    data: bands,
  }
}

/** The tooltip line for a flagged day, or nothing. */
export function feedQualityTooltip(date: string, flagged: ReadonlyMap<string, string>): string {
  const reason = flagged.get(date)
  return reason ? `<br/><em>Feed looked wrong this day: ${reason}. Figures may be distorted.</em>` : ''
}
