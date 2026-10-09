/**
 * Config per widget type of the Overview & Population group, field for field as
 * `registry/widget-contract.json` declares them (a spec checks the two agree).
 *
 * Type aliases rather than interfaces: an interface has no implicit index signature, so it would not
 * satisfy `WidgetConfig` (`Record<string, unknown>`), and giving it one would make every key a
 * string and stop `fields` from catching a misspelt field name.
 */
import type { KpiMetric, KpiNumberFormat } from './kpiMetrics'

/** metric.kpi */
export type KpiConfig = {
  metric: KpiMetric
  numberFormat: KpiNumberFormat
  /** Decimals of a percentage: the TAC coverage figure and the "% of bindings" qualifiers. */
  precision: number
  showDefinition: boolean
  showQualifier: boolean
}

/** metric.kpiSummary */
export type KpiSummaryConfig = {
  metrics: KpiMetric[]
  numberFormat: KpiNumberFormat
  precision: number
  showDefinition: boolean
}

export type TrendGrain = 'day' | 'week' | 'month'

/** population.trend */
export type PopulationTrendConfig = {
  grain: TrendGrain
  chart: 'line' | 'area'
  showFlagged: boolean
  showMissing: boolean
}
