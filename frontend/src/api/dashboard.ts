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
  /**
   * Which delivery these figures were computed from.
   *
   * The dashboard serves the newest delivery whose marts are *complete*, so during a rebuild it
   * shows the previous one while the import history already lists the newer day. Without this the
   * page would show older numbers under a newer heading and say nothing about it.
   */
  deliverySequence: number
  /** That delivery's business date. Null for the initial dump, which covers a window. */
  deliveryDate: string | null
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
  /**
   * The day a daily file last said anything about this binding, or null when none ever has.
   *
   * Null means the binding is active only because the initial dump listed it and nothing has
   * removed it since. The dump covers a 30-day window rather than an instant, so it can list
   * several handsets one subscriber used that month — which is why one SIM can legitimately show
   * more than one active binding.
   */
  lastChangeDate: string | null
}

export interface LookupResult {
  msisdn: string
  wellFormed: boolean
  count: number
  /** Masked when the caller lacks identifier.reveal: the IMSI and IMEI are then redacted. */
  identifiers: 'Masked' | 'Full'
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

export type CountBy = 'bindings' | 'subscribers' | 'handsets'

export function useTopDimension(
  dimension: MaybeRefOrGetter<Dimension>,
  filters: MaybeRefOrGetter<DashboardFilters>,
  limit: MaybeRefOrGetter<number> = 10,
  countBy: MaybeRefOrGetter<CountBy> = 'bindings',
) {
  return useQuery({
    queryKey: [
      'top',
      computed(() => toValue(dimension)),
      computed(() => toValue(filters)),
      computed(() => toValue(limit)),
      computed(() => toValue(countBy)),
    ],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>(
        `/api/v1/dashboard/top/${toValue(dimension)}`,
        { limit: toValue(limit), countBy: toValue(countBy), ...toValue(filters) },
        signal,
      ),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export function useDistribution(
  dimension: MaybeRefOrGetter<Dimension>,
  filters: MaybeRefOrGetter<DashboardFilters>,
  countBy: MaybeRefOrGetter<CountBy> = 'bindings',
) {
  return useQuery({
    queryKey: [
      'distribution',
      computed(() => toValue(dimension)),
      computed(() => toValue(filters)),
      computed(() => toValue(countBy)),
    ],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>(
        `/api/v1/dashboard/distribution/${toValue(dimension)}`,
        { countBy: toValue(countBy), ...toValue(filters) },
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
export function useDeviceClassMix(countBy: MaybeRefOrGetter<CountBy> = 'bindings') {
  return useQuery({
    queryKey: ['device-class-mix', computed(() => toValue(countBy))],
    queryFn: ({ signal }) =>
      api.get<DimensionCount[]>(
        '/api/v1/dashboard/device-class-mix',
        { countBy: toValue(countBy) },
        signal,
      ),
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
export function useCapabilities(countBy: MaybeRefOrGetter<CountBy> = 'bindings') {
  return useQuery({
    queryKey: ['capabilities', computed(() => toValue(countBy))],
    queryFn: ({ signal }) =>
      api.get<CapabilitySupport[]>(
        '/api/v1/dashboard/capabilities',
        { countBy: toValue(countBy) },
        signal,
      ),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export interface DailyChange {
  /** The day the changes actually happened, from the source filename. */
  date: string
  added: number
  removed: number
  net: number
  /** Running active-binding total, starting from the initial dump. */
  cumulative: number
  unknownDeviceRows: number
}

export interface DailyChurn {
  date: string
  /** Numbers that moved to a different SIM that day. */
  simChanges: number
  /** Numbers that moved to a different handset that day. */
  deviceChanges: number
}

/**
 * Daily change history, on a real calendar axis.
 *
 * Only possible since the source began supplying dated filenames - before that the axis
 * could only honestly be labelled "delivery sequence".
 */
export function useDailyChanges() {
  return useQuery({
    queryKey: ['daily-changes'],
    queryFn: ({ signal }) =>
      api.get<DailyChange[]>('/api/v1/dashboard/daily-changes', undefined, signal),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

export function useDailyChurn() {
  return useQuery({
    queryKey: ['daily-churn'],
    queryFn: ({ signal }) =>
      api.get<DailyChurn[]>('/api/v1/dashboard/daily-churn', undefined, signal),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}

/** Biggest gainers and biggest losers by net binding change over the loaded period. */
/** Which figure the vendor widget ranks by. */
export type VendorRanking = 'movement' | 'share' | 'growth'

/**
 * One vendor, measured three ways.
 *
 * Every field arrives whichever ranking was asked for, so switching the widget's mode is a
 * client-side re-sort rather than a round trip.
 */
export interface VendorMovementRow {
  vendor: string
  /** Add events in the window. */
  added: number
  /** Remove events in the window. */
  removed: number
  /**
   * Added minus removed — **events, not devices**.
   *
   * One SIM moved between two handsets thirty times contributes thirty of each and changes the
   * population by nothing. This is why HMD tops the movement ranking with more adds than it has
   * devices on the network.
   */
  net: number
  /** Active bindings now. */
  population: number
  sharePercent: number
  /** Active bindings in the first delivery on record. */
  populationAtStart: number
  populationChange: number
  /** Null when the vendor started at zero: a change from nothing has no percentage. */
  populationChangePercent: number | null
  /**
   * The vendor's percentage change minus the network's.
   *
   * The figure that means something. The whole network fell 9.29% between the first delivery and
   * now, so a vendor down 6.4% actually gained almost three points of share. Absolute change
   * alone reads as universal decline.
   */
  vsNetworkPoints: number | null
  /** Net movement as a percentage of the vendor's own population; null when that is zero. */
  netPercentOfPopulation: number | null
}

export interface VendorMovementResponse {
  from: string | null
  to: string | null
  /** The span the feed actually covers, so the date pickers can be bounded by it. */
  earliestAvailable: string | null
  latestAvailable: string | null
  networkPopulation: number
  networkPopulationAtStart: number
  networkChangePercent: number
  /**
   * True when the comparison point is the initial dump.
   *
   * That dump covers a 30-day window rather than an instant — it averages 1.57 handsets per SIM —
   * so a large part of every vendor's apparent decline is that over-count being resolved rather
   * than devices leaving. The widget says so.
   */
  startIsInitialDump: boolean
  rows: VendorMovementRow[]
}

export interface VendorMovementFilters {
  from?: string | null
  to?: string | null
  rank?: VendorRanking
  limit?: number
}

export function useVendorMovement(filters: MaybeRefOrGetter<VendorMovementFilters>) {
  return useQuery({
    queryKey: ['vendor-movement', computed(() => toValue(filters))],
    queryFn: ({ signal }) =>
      api.get<VendorMovementResponse>(
        '/api/v1/dashboard/vendor-movement',
        { ...toValue(filters) },
        signal,
      ),
    staleTime: DAILY_DATA_STALE_TIME,
  })
}


/** Subscriber lookup. POST so the number never appears in a URL. */
export function lookupMsisdn(msisdn: string, signal?: AbortSignal) {
  return api.post<LookupResult>('/api/v1/lookup/msisdn', { msisdn }, signal)
}
