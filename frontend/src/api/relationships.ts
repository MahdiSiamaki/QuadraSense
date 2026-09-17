import { useMutation } from '@tanstack/vue-query'
import { api } from './client'

/** Which of the three identifiers a node is. */
export type RelatedKind = 'Msisdn' | 'Imsi' | 'Imei'

/** One identifier connected to the one being explored. */
export interface RelatedNode {
  value: string
  kind: RelatedKind
  /** How many bindings join it to the centre. */
  bindings: number
  /** How many of those are still in force. */
  activeBindings: number
  /**
   * The day a daily file last said anything about it.
   *
   * Null means no daily file ever has: it is present only because the initial dump listed it and
   * nothing has removed it since. That is a real answer, not a missing one.
   */
  lastConfirmed: string | null
  tac?: string | null
  brand?: string | null
  marketingName?: string | null
  deviceType?: string | null
}

/**
 * Two IMEIs shown to be the two radios of one physical handset.
 *
 * `sharedSubscribers` is the evidence, not a detail: it is the number of phone numbers seen on
 * both, and it is what separates a real pair from two handsets that merely came off the line one
 * after another.
 */
export interface PairedHandset {
  imei: string
  marketingName: string | null
  sharedSubscribers: number
}

export interface RelationshipGraph {
  centre: string
  kind: RelatedKind
  /** False when the identifier is well-formed but appears nowhere in the data. */
  found: boolean
  subscribers: RelatedNode[]
  sims: RelatedNode[]
  handsets: RelatedNode[]
  paired: PairedHandset[]
  /** Sections the caller may not see, named rather than silently returned empty. */
  withheld: string[]
  truncated: boolean
  elapsedMs: number
  rowsExamined: number
}

/**
 * Explore one identifier.
 *
 * POST, and a mutation rather than a query, for the same reason the subscriber lookup is: the
 * identifier must not reach a URL, and therefore must not reach server access logs, proxy logs or
 * browser history. A cached query keyed on the identifier would also keep it in memory across
 * navigations, which is the opposite of what this data wants.
 */
export function useRelationshipExplorer() {
  return useMutation({
    mutationFn: (identifier: string) =>
      api.post<RelationshipGraph>('/api/v1/relationships/explore', { identifier }),
  })
}
