/**
 * Config per widget type of the Risk & Investigation group, field for field as
 * `registry/widget-contract.json` declares them.
 */
import type { RiskView } from '@/api/risk'
import type { RuleName } from './shared'

/** risk.status */
export type RiskStatusConfig = {
  showRules: boolean
}

/** risk.overview */
export type RiskOverviewConfig = {
  rules: RuleName[]
  display: 'table' | 'bar'
}

/** risk.distribution */
export type RiskDistributionConfig = {
  rule: RuleName
  values: 'clean' | 'raw' | 'both'
  showThreshold: boolean
}

/** risk.deviceTypes - every rule but the per-number one, which has no handset. */
export type RiskDeviceTypesConfig = {
  rule: Exclude<RuleName, 'RepeatedSimChange7'>
  limit: number
  display: 'table' | 'bar'
}

/** risk.simChangeEvidence */
export type SimChangeEvidenceConfig = {
  series: Array<'changes' | 'setAside'>
  showFlagged: boolean
}

/** risk.evidenceQuality */
export type EvidenceQualityConfig = {
  rules: RuleName[]
}

/** risk.list */
export type RiskListConfig = {
  rule: RuleName
  view: RiskView
  pageSize: number
  deviceTypes: string[]
  thresholdOverride: number | null
  tacsThresholdOverride: number | null
}
