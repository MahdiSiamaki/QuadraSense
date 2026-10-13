/**
 * Config per widget type of the Data Quality group, field for field as
 * `registry/widget-contract.json` declares them.
 */
import type { FeedQualityCheck, QualityGroup } from '@/api/quality'

/** quality.enrichment */
export type EnrichmentConfig = {
  display: 'figures' | 'table'
}

/** quality.feedTrend */
export type FeedTrendConfig = {
  checks: FeedQualityCheck[]
  measure: 'rate' | 'count'
  display: 'chart' | 'table'
  showReference: boolean
}

/** quality.flaggedDays */
export type FlaggedDaysConfig = {
  include: Array<'flagged' | 'missing'>
  limit: number
}

/** quality.signals */
export type SignalsConfig = {
  groups: QualityGroup[]
  unit: 'bindings' | 'numbers' | 'sims' | 'imeis'
  display: 'table' | 'bar'
}
