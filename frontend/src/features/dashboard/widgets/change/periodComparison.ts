/**
 * One figure for two periods, and the change between them - computed from the daily series the
 * dashboard already has (`/daily-changes`, `/daily-churn`): about 240 rows a year, no new aggregation
 * (ADR-016 §12).
 *
 * Flow metrics (added, removed, net, SIM and handset changes) are summed over the period's delivered
 * days, or averaged per delivered day. `population` is a level, not a flow: active bindings on the
 * period's last delivered day, or their average over its delivered days. (A "population change" would
 * be the sum of net exactly - the running total is walked back by each day's net - so it is not a
 * separate metric.)
 *
 * A day the source never delivered has no row and adds nothing, so a total over a period with missing
 * days is short. The result says when the two periods have different numbers of delivered days, and
 * the widget recommends the daily average then; it never fills a missing day in.
 */
import type { DailyChange, DailyChurn } from '@/api/dashboard'
import { addDays } from '@/features/dashboard/composables/lens'
import { daysInclusive } from '../overview/shared/series'
import type { ComparisonMetric, PeriodComparisonConfig } from './config'

export interface PeriodWindow {
  from: string
  to: string
}

export interface PeriodFigure extends PeriodWindow {
  calendarDays: number
  deliveredDays: number
  /** Null when no day in the period was delivered. */
  value: number | null
}

export interface Comparison {
  current: PeriodFigure
  baseline: PeriodFigure | null
  absolute: number | null
  /** Null when the baseline is zero or missing: a change from nothing has no percentage. */
  percent: number | null
  /** Why there is nothing to compare with, in words; null when there is. */
  unavailable: string | null
  /** A total over periods with different numbers of delivered days. */
  uneven: boolean
}

export const CHURN_METRICS: readonly ComparisonMetric[] = ['simChanges', 'deviceChanges']

export function usesChurn(metric: ComparisonMetric): boolean {
  return CHURN_METRICS.includes(metric)
}

/** The same number of calendar days, ending the day before `current` starts. */
export function previousWindow(current: PeriodWindow): PeriodWindow {
  const days = daysInclusive(current.from, current.to)
  return { from: addDays(current.from, -days), to: addDays(current.from, -1) }
}

type Row = DailyChange | DailyChurn

function inWindow<T extends Row>(rows: readonly T[], window: PeriodWindow): T[] {
  return rows.filter((r) => r.date >= window.from && r.date <= window.to).sort((a, b) => (a.date < b.date ? -1 : 1))
}

function valueOf(row: Row, metric: ComparisonMetric): number {
  switch (metric) {
    case 'added':
      return (row as DailyChange).added
    case 'removed':
      return (row as DailyChange).removed
    case 'net':
      return (row as DailyChange).net
    case 'population':
      return (row as DailyChange).cumulative
    case 'simChanges':
      return (row as DailyChurn).simChanges
    case 'deviceChanges':
      return (row as DailyChurn).deviceChanges
  }
}

/** One period's figure. */
export function periodFigure(
  rows: readonly Row[],
  window: PeriodWindow,
  metric: ComparisonMetric,
  aggregate: PeriodComparisonConfig['aggregate'],
): PeriodFigure {
  const days = inWindow(rows, window)
  const base = { ...window, calendarDays: daysInclusive(window.from, window.to), deliveredDays: days.length }
  if (days.length === 0) return { ...base, value: null }

  const values = days.map((r) => valueOf(r, metric))
  const sum = values.reduce((a, b) => a + b, 0)
  if (metric === 'population') {
    return { ...base, value: aggregate === 'total' ? values[values.length - 1]! : sum / values.length }
  }
  return { ...base, value: aggregate === 'total' ? sum : sum / values.length }
}

/**
 * The comparison the widget draws.
 *
 * @param current The dashboard's range, resolved to dates (an open bound is the series' own edge).
 * @param dataStart The first delivered day, so a baseline wholly before it is said to be, not shown as
 *   a zero.
 */
export function comparePeriods(input: {
  config: Pick<PeriodComparisonConfig, 'metric' | 'aggregate' | 'baseline' | 'baselineFrom' | 'baselineTo'>
  current: PeriodWindow
  rows: readonly Row[]
  dataStart: string | null
}): Comparison {
  const { config, rows } = input
  const current = periodFigure(rows, input.current, config.metric, config.aggregate)
  const none = (unavailable: string): Comparison => ({
    current,
    baseline: null,
    absolute: null,
    percent: null,
    unavailable,
    uneven: false,
  })

  let window: PeriodWindow
  if (config.baseline === 'custom') {
    if (!config.baselineFrom || !config.baselineTo) return none('Set both baseline dates in the widget’s settings.')
    if (config.baselineFrom > config.baselineTo) return none('The baseline starts after it ends.')
    window = { from: config.baselineFrom, to: config.baselineTo }
  } else {
    window = previousWindow(input.current)
  }

  if (input.dataStart !== null && window.to < input.dataStart) {
    return none(`The baseline (${window.from} to ${window.to}) is before the first daily file, ${input.dataStart}.`)
  }

  const baseline = periodFigure(rows, window, config.metric, config.aggregate)
  if (baseline.value === null) {
    return { ...none(`No day from ${window.from} to ${window.to} was delivered.`), baseline }
  }
  if (current.value === null) {
    return { ...none('No day in the dashboard’s range was delivered.'), baseline }
  }

  const absolute = current.value - baseline.value
  const percent = baseline.value === 0 ? null : (absolute / Math.abs(baseline.value)) * 100
  const uneven = config.aggregate === 'total' && config.metric !== 'population' && current.deliveredDays !== baseline.deliveredDays
  return { current, baseline, absolute, percent, unavailable: null, uneven }
}
