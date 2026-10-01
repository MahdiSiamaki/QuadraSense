import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/* Mirrors Sqm.Contracts.Quality. */

export type FeedQualityCheck = 'ShiftedImei' | 'MultiNumberSim' | 'MalformedImei' | 'UnknownDevice'

export interface FeedQualityFinding {
  check: FeedQualityCheck
  /** 0 to 1. */
  rate: number
  /** Above this the day is flagged; null when there were no reference days to judge by. */
  threshold: number | null
  /** The reference days' median rate: what an ordinary day looks like. */
  referenceMedian: number | null
  flagged: boolean
  /** What was measured, against what - written for a person, shown as is. */
  explanation: string
}

export interface FeedQualityDay {
  date: string
  rows: number
  sims: number
  unknownDeviceRows: number
  malformedImeiRows: number
  unknownTacRows: number
  shiftedImeiRows: number
  multiNumberSims: number
  multiNumberSimRows: number
  flagged: boolean
  findings: FeedQualityFinding[]
}

export interface FeedQualityResponse {
  reference: { from: string; to: string; days: number; multiplier: number; minimumRate: number }
  days: FeedQualityDay[]
}

/** Short names for the checks, for a chart's tooltip. */
export const CHECK_LABELS: Record<FeedQualityCheck, string> = {
  ShiftedImei: 'IMEIs shifted by one digit',
  MultiNumberSim: 'SIMs with several numbers',
  MalformedImei: 'malformed IMEIs',
  UnknownDevice: 'unknown-device rows',
}

/**
 * Every measured day's file, judged against the ordinary days.
 *
 * `enabled` because the route is behind import.view and the dashboard is shown to people without
 * it; for them the charts simply carry no marks, rather than a 403 in the console.
 */
export function useFeedQuality(options: { enabled?: MaybeRefOrGetter<boolean> } = {}) {
  return useQuery({
    queryKey: ['feed-quality'],
    queryFn: ({ signal }) => api.get<FeedQualityResponse>('/api/v1/quality/days', undefined, signal),
    // One row per day, changed only by an import.
    staleTime: 5 * 60_000,
    enabled: computed(() => toValue(options.enabled ?? true)),
  })
}

/** Flagged days, as date to a short reason for a tooltip. */
export function flaggedDays(response: FeedQualityResponse | undefined): Map<string, string> {
  const out = new Map<string, string>()
  for (const day of response?.days ?? []) {
    const reasons = day.findings.filter((f) => f.flagged).map((f) => CHECK_LABELS[f.check])
    if (reasons.length) out.set(day.date, reasons.join(', '))
  }
  return out
}
