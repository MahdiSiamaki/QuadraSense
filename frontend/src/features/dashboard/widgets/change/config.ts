/**
 * Config per widget type of the Change & Movement group, field for field as
 * `registry/widget-contract.json` declares them.
 */
import type { VendorRanking } from '@/api/dashboard'

export type ChangeSeries = 'added' | 'removed' | 'net' | 'cumulative'

/** change.daily */
export type ChangeDailyConfig = {
  series: ChangeSeries[]
  chart: 'bar' | 'line'
  showFlagged: boolean
  showMissing: boolean
}

/** change.simHandset */
export type SimHandsetConfig = {
  series: Array<'simChanges' | 'deviceChanges'>
  chart: 'line' | 'bar'
  showFlagged: boolean
}

/** change.vendorMovement */
export type VendorMovementConfig = {
  rank: VendorRanking
  limit: number
  display: 'chart' | 'table'
  normalised: boolean
}

export type ComparisonMetric = 'added' | 'removed' | 'net' | 'simChanges' | 'deviceChanges' | 'population'

/** change.periodComparison */
export type PeriodComparisonConfig = {
  metric: ComparisonMetric
  baseline: 'previous' | 'custom'
  baselineFrom: string | null
  baselineTo: string | null
  aggregate: 'total' | 'dailyAverage'
  change: 'absolute' | 'percent' | 'both'
}
