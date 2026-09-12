import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/*
  These types mirror Sqm.Contracts. Once Phase 2 wires up openapi-typescript they
  will be generated from the API's own OpenAPI document and this file becomes
  hooks only - hand-written types are a temporary bridge, not the plan.
*/

export interface KpiSummary {
  activeBindings: number
  /** Distinct MSISDN - phone numbers. */
  distinctSubscribers: number
  /** Distinct IMSI - SIM cards. Slightly higher than numbers; the gap is SIM swaps. */
  distinctSims: number
  /** Distinct 14-digit IMEI - handsets. Excludes the 000000 sentinel. */
  distinctDevices: number
  unknownDeviceBindings: number
  /** Numeric IMEI that is not 14 digits: a defect, distinct from the 000000 sentinel. */
  malformedImeiBindings: number
  /** Exact count, not derived from the percentage - see the contract for why. */
  tacMatchedBindings: number
  tacCoveragePercent: number
}

export interface DimensionCount {
  key: string
  count: number
  percent: number
}

export interface ChangePoint {
  sequence: number
  /** Null until the source supplies real dates. The UI must not invent one. */
  dataDate: string | null
  added: number
  removed: number
  net: number
}

export interface BindingRow {
  msisdn: string
  imsi: string
  imei: string
  tac: string | null
  manufacturer: string | null
  marketingName: string | null
  deviceType: string | null
  isActive: boolean
}

export interface LookupResult {
  msisdn: string
  wellFormed: boolean
  count: number
  bindings: BindingRow[]
}

export type Dimension =
  | 'manufacturer'
  | 'vendorCanonical'
  | 'marketingName'
  | 'deviceType'
  | 'operatingSystem'
  | 'tac'

export interface DashboardFilters {
  vendor?: string
  deviceType?: string
  operatingSystem?: string
  tac?: string
  msisdnPrefix?: string
  includeUnknownDevice?: boolean
}

/**
 * Measured latency against the real 126M-row dataset: KPI ~6.5s, top-dimension
 * ~5.3s, before the mart layer exists. Two consequences encoded below:
 *
 *  - `staleTime` is long, because re-running a six-second aggregate on every
 *    focus change would make the app feel worse, not fresher. The data changes
 *    once a day.
 *  - Every query takes the AbortSignal TanStack Query provides, so navigating
 *    away actually cancels the request instead of leaving it to finish unseen.
 */
const DAILY_DATA_STALE_TIME = 5 * 60 * 1000

export function useKpiSummary(filters: MaybeRefOrGetter<DashboardFilters>) {
  return useQuery({
    queryKey: ['kpi', computed(() => toValue(filters))],
    queryFn: ({ signal }) => api.get<KpiSummary>('/api/v1/dashboard/kpi', { ...toValue(filters) }, signal),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export function useTopDimension(
  dimension: MaybeRefOrGetter<Dimension>,
  filters: MaybeRefOrGetter<DashboardFilters>,
  limit: MaybeRefOrGetter<number> = 10,
) {
  return useQuery({
    queryKey: [
      'top',
      computed(() => toValue(dimension)),
      computed(() => toValue(filters)),
      computed(() => toValue(limit)),
    ],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>(
        `/api/v1/dashboard/top/${toValue(dimension)}`,
        { limit: toValue(limit), ...toValue(filters) },
        signal,
      ),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export function useDistribution(
  dimension: MaybeRefOrGetter<Dimension>,
  filters: MaybeRefOrGetter<DashboardFilters>,
) {
  return useQuery({
    queryKey: ['distribution', computed(() => toValue(dimension)), computed(() => toValue(filters))],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>(
        `/api/v1/dashboard/distribution/${toValue(dimension)}`,
        { ...toValue(filters) },
        signal,
      ),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

/**
 * The handset-versus-machine mix.
 *
 * Read from a pre-computed 8-row table, so this is effectively free (~106 ms end
 * to end, most of it HTTP).
 */
export function useDeviceClassMix() {
  return useQuery({
    queryKey: ['device-class-mix'],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>('/api/v1/dashboard/device-class-mix', undefined, signal),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export interface CapabilitySupport {
  capability: string
  supported: number
  unsupported: number
  /** Cannot be assessed: unknown device, unregistered TAC, or the GSMA record is silent. */
  unknown: number
  /** Share of devices we can actually assess. The headline figure. */
  percentOfAssessable: number
  percentOfAll: number
  /** How much of the base this capability is knowable for. Essential context. */
  coveragePercent: number
}

/**
 * Network and SIM capability: LTE, 5G, eSIM.
 *
 * VoLTE is absent because the GSMA dataset does not carry it - bandDetails mentions it
 * in 2 rows out of 270,166, and the IMS columns describe emergency calling, not VoLTE.
 * Reporting a guess here would be worse than reporting nothing.
 */
export function useCapabilities() {
  return useQuery({
    queryKey: ['capabilities'],
    queryFn: ({ signal }) =>
      api.get<CapabilitySupport[]>('/api/v1/dashboard/capabilities', undefined, signal),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

/** Subscriber lookup. POST so the number never appears in a URL. */
export function lookupMsisdn(msisdn: string, signal?: AbortSignal) {
  return api.post<LookupResult>('/api/v1/lookup/msisdn', { msisdn }, signal)
}
