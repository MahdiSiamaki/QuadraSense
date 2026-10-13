/**
 * The four feed-quality checks as daily series.
 *
 * Counts are all **rows** - a SIM with several numbers is counted by its rows, not as one SIM - so
 * the four share one unit and one axis. Rates are each check's own rate as the monitor judged it
 * (0 to 1), and the reference is the line above which the monitor flags a day.
 */
import type { FeedQualityCheck, FeedQualityDay, FeedQualityResponse } from '@/api/quality'

export const CHECKS: readonly FeedQualityCheck[] = ['ShiftedImei', 'MultiNumberSim', 'MalformedImei', 'UnknownDevice']

/** Colour per check, by entity and never by rank: a check keeps its colour whichever are drawn. */
export const CHECK_COLOURS: Record<FeedQualityCheck, string> = {
  ShiftedImei: '--viz-1',
  MultiNumberSim: '--viz-2',
  MalformedImei: '--viz-3',
  UnknownDevice: '--viz-4',
}

export function checkCount(day: FeedQualityDay, check: FeedQualityCheck): number {
  switch (check) {
    case 'ShiftedImei':
      return day.shiftedImeiRows
    case 'MultiNumberSim':
      return day.multiNumberSimRows
    case 'MalformedImei':
      return day.malformedImeiRows
    case 'UnknownDevice':
      return day.unknownDeviceRows
  }
}

/** The day's rate for a check, or null when the monitor did not measure it that day. */
export function checkRate(day: FeedQualityDay, check: FeedQualityCheck): number | null {
  return day.findings.find((f) => f.check === check)?.rate ?? null
}

/**
 * The line a check's rate is flagged above, from the newest day that states one. Null when no day
 * had reference days to judge by.
 */
export function checkThreshold(response: FeedQualityResponse | undefined, check: FeedQualityCheck): number | null {
  const days = response?.days ?? []
  for (let i = days.length - 1; i >= 0; i--) {
    const t = days[i]!.findings.find((f) => f.check === check)?.threshold
    if (t !== null && t !== undefined) return t
  }
  return null
}
