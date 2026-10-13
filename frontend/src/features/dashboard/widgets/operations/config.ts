/**
 * Config per widget type of the Data Operations group, field for field as
 * `registry/widget-contract.json` declares them.
 */
import type { ImportStatus } from '@/api/imports'

/** ops.freshness */
export type FreshnessConfig = {
  showPipeline: boolean
  showTac: boolean
}

/** ops.recentImports */
export type RecentImportsConfig = {
  source: 'any' | 'SQM' | 'TAC'
  statuses: ImportStatus[]
  limit: number
}

/** ops.importStatus - nothing to configure. */
export type ImportStatusConfig = Record<string, never>

/** ops.workerHealth */
export type WorkerHealthConfig = {
  showPendingRebuild: boolean
}

/** ops.blockages */
export type BlockagesConfig = {
  limit: number
}

/** ops.tacVersion */
export type TacVersionConfig = {
  showPending: boolean
}
