/**
 * Config per widget type of the Analyst Tools group, field for field as
 * `registry/widget-contract.json` declares them.
 */
import type { ExplorerQueryRequest } from '@/api/explorer'

/** tools.explorerAggregate - the query is aggregated and names no identifier (the server checks). */
export type ExplorerAggregateConfig = {
  query: ExplorerQueryRequest | null
  /** For the event log: the last N days of data, stored as N and turned into dates when it runs. */
  lastDays: number | null
  rows: number
  display: 'table' | 'bar'
}

/** tools.quickLookup - a launcher; what is typed into it is never stored. */
export type QuickLookupConfig = {
  target: 'msisdn' | 'imsi' | 'relationships' | 'tac'
}

/** tools.modelNetworkAge - a TAC is a model code, not a person's identifier. */
export type ModelNetworkAgeConfig = {
  tac: string
  showDetail: boolean
}
