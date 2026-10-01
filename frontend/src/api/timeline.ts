import { useMutation } from '@tanstack/vue-query'
import { api } from './client'

/*
  Timelines: every binding of one number, SIM or handset over time, from the binding history.

  A POST with the identifier in the body, never a URL - the same rule as every lookup. A mutation
  rather than a cached query, as the IMSI search is: it is audited, and a person's identifier should
  not sit in a cache after the page has moved on.
*/

export type TimelineKind = 'msisdn' | 'imsi' | 'imei'

export interface TimelinePeriod {
  start: string
  /** The start is the initial dump's window, not a known day. */
  startsInDump: boolean
  /** Null while the feed has not removed it - which is not proof it is in the handset today. */
  end: string | null
}

export interface TimelineEvent {
  date: string
  change: 'add' | 'remove'
  /** "redundant": an add of a binding already held. "orphan": a remove of one not held. */
  effect: 'applied' | 'redundant' | 'orphan'
}

export interface TimelineBinding {
  /** Null when withheld; masked without identifier.reveal. */
  msisdn: string | null
  imsi: string | null
  imei: string | null
  tac: string | null
  brand: string | null
  model: string | null
  active: boolean
  inDump: boolean
  firstSeen: string | null
  firstSeenIsDumpWindow: boolean
  lastChange: string | null
  periods: TimelinePeriod[]
  events: TimelineEvent[]
}

export interface TimelineGroup {
  kind: TimelineKind
  key: string
  /** Complete and unmasked: can be opened. */
  drillable: boolean
  tac: string | null
  brand: string | null
  model: string | null
  bindings: number
  active: boolean
  firstSeen: string | null
  firstSeenIsDumpWindow: boolean
  lastChange: string | null
  periods: TimelinePeriod[]
}

export interface TimelineSummary {
  firstSeen: string | null
  firstSeenIsDumpWindow: boolean
  lastChange: string | null
  bindings: number
  activeBindings: number
  numbers: number
  sims: number
  /** IMEIs - not phones: a dual-SIM phone has two. */
  handsets: number
  redundantAdds: number
  orphanRemoves: number
  stateDisagreements: number
}

export interface Timeline {
  kind: TimelineKind
  identifier: string
  tac: string | null
  brand: string | null
  model: string | null
  summary: TimelineSummary
  groups: TimelineGroup[]
  bindings: TimelineBinding[]
  truncated: boolean
  maxBindings: number
  withheld: TimelineKind[]
  masked: boolean
  dumpWindowStart: string
  dumpWindowEnd: string
  dataThrough: string | null
  elapsedMs: number
  rowsRead: number
}

export function useTimeline() {
  return useMutation({
    mutationFn: (identifier: string) => api.post<Timeline>('/api/v1/timeline', { identifier }),
  })
}

/** What each kind is called on screen. */
export const KIND_LABEL: Record<TimelineKind, { one: string; many: string }> = {
  msisdn: { one: 'Number', many: 'Numbers' },
  imsi: { one: 'SIM', many: 'SIMs' },
  imei: { one: 'Handset (IMEI)', many: 'Handsets (IMEIs)' },
}
