import { keepPreviousData, useMutation, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/*
  The Devices module.

  A DEVICE HERE IS A MODEL, identified by its 8-digit TAC — not a handset you can hold. A handset
  is an IMEI, and its first eight digits are its TAC: checked against every one of the 284,341,927
  well-formed rows of current state, with zero exceptions. That one fact is why a device's handsets
  are a contiguous range of an IMEI-ordered table rather than a filter over 295 million rows.

  The search is a POST, like the other identifier searches, because the term may be a real
  subscriber number or SIM identity. A GET would put it into web-server access logs, browser
  history and any Referer the page emits, where no amount of later masking can reach it.
*/

/** The 8-digit type allocation code. Every TAC in the GSMA export is exactly this long. */
export const TAC_LENGTH = 8

/** Digits shared by every IMSI in the dataset: MCC 432, MNC 11. */
export const IMSI_CONSTANT_PREFIX = '43211'

export type DeviceSort =
  | 'bindings'
  | 'handsets'
  | 'sims'
  | 'subscribers'
  | 'model'
  | 'manufacturer'
  | 'tac'
  | 'lastSeen'

export interface DeviceSummary {
  tac: string
  manufacturer: string | null
  /** Curated vendor name, which collapses the six spellings of Samsung. */
  vendor: string | null
  brand: string | null
  model: string | null
  marketingName: string | null
  deviceType: string | null
  operatingSystem: string | null
  /** Active number + SIM + handset triples. */
  bindings: number
  /** Distinct handsets. Below `bindings` whenever a handset carries more than one SIM. */
  handsets: number
  sims: number
  subscribers: number
  /**
   * First day a daily file named a binding of this model.
   *
   * Null means none ever has: the model is present only because the initial dump listed it, which
   * is true of a large share of the tail. It does not mean the model is new.
   */
  firstSeen: string | null
  lastSeen: string | null
  hasImage: boolean
}

/** What a typed search term turned out to be, when it was an identifier rather than a name. */
export interface DeviceSearchResolution {
  kind: 'Tac' | 'Msisdn' | 'Imei' | 'Imsi' | 'UnknownDeviceSentinel' | 'Text'
  models: number
  /** False when the caller may not resolve identifiers of this kind. */
  permitted: boolean
  note: string | null
}

export interface SearchTiming {
  elapsedMs: number
  /** Rows the engine read. The number that tells an indexed lookup from a scan. */
  rowsExamined: number
}

export interface DeviceListResponse {
  total: number
  page: number
  pageSize: number
  items: DeviceSummary[]
  resolution: DeviceSearchResolution | null
  timing: SearchTiming
}

export interface DeviceCapabilities {
  /** Null means the GSMA record does not say — which for IMS emergency covers 94% of TACs. */
  lte: boolean | null
  fiveG: boolean | null
  esim: boolean | null
  imsEmergency: boolean | null
}

export interface DevicePopulation {
  bindings: number
  handsets: number
  sims: number
  subscribers: number
  bindingsAtStart: number
  /** Null when the model had no bindings at the first delivery: there is no percentage of zero. */
  changePercent: number | null
  /**
   * The change minus the network's own, in points. The figure that makes growth readable. Null
   * whenever the change is.
   */
  vsNetworkPoints: number | null
  firstSeen: string | null
  lastSeen: string | null
}

export interface DeviceDetail {
  tac: string
  manufacturer: string | null
  vendor: string | null
  brand: string | null
  model: string | null
  marketingName: string | null
  deviceType: string | null
  operatingSystem: string | null
  oem: string | null
  organisationId: string | null
  allocationDate: string | null
  lastUpdatedDate: string | null
  bluetooth: string | null
  nfc: string | null
  wlan: string | null
  simSlots: string | null
  imeiQuantity: string | null
  bands: string | null
  capabilities: DeviceCapabilities
  population: DevicePopulation
  /** False when the network has seen this TAC but the active GSMA snapshot does not list it. */
  knownToGsma: boolean
  hasImage: boolean
  imageSourceNote: string | null
  imageUpdatedAt: string | null
  tacVersionId: number
}

export interface DeviceTimelinePoint {
  date: string
  added: number
  removed: number
}

export interface DeviceTimelineResponse {
  tac: string
  from: string | null
  to: string | null
  earliestAvailable: string | null
  latestAvailable: string | null
  points: DeviceTimelinePoint[]
  timing: SearchTiming
}

export interface DeviceIdentifierRow {
  imei: string
  imsi: string
  msisdn: string
  isActive: boolean
  lastChangeDate: string | null
}

export interface DeviceIdentifiersResponse {
  tac: string
  total: number
  page: number
  pageSize: number
  identifiers: 'Masked' | 'Full'
  items: DeviceIdentifierRow[]
  timing: SearchTiming
}

export interface DeviceFacetValue {
  value: string
  models: number
}

export interface DeviceFacets {
  deviceTypes: DeviceFacetValue[]
  manufacturers: DeviceFacetValue[]
  operatingSystems: DeviceFacetValue[]
}

export interface DeviceSearchFilters {
  query?: string | null
  manufacturer?: string | null
  brand?: string | null
  deviceType?: string | null
  operatingSystem?: string | null
  minBindings?: number
  sort?: DeviceSort
  descending?: boolean
  page?: number
  pageSize?: number
}

/**
 * The device catalogue.
 *
 * A query rather than a mutation, unlike the IMSI search, and the difference is deliberate. This
 * is a browsable list: paging, sorting and filtering should all be cached and instant on the way
 * back. The term only becomes an identifier when somebody types one, and in that case the
 * server audits it — the caching happens in the browser and reaches no further.
 */
export function useDeviceSearch(filters: MaybeRefOrGetter<DeviceSearchFilters>) {
  return useQuery({
    queryKey: ['devices', computed(() => toValue(filters))],
    queryFn: ({ signal }) => {
      const f = toValue(filters)
      return api.post<DeviceListResponse>(
        '/api/v1/devices/search',
        {
          query: f.query || null,
          manufacturer: f.manufacturer || null,
          brand: f.brand || null,
          deviceType: f.deviceType || null,
          operatingSystem: f.operatingSystem || null,
          minBindings: f.minBindings ?? 0,
          sort: f.sort ?? 'bindings',
          descending: f.descending ?? true,
          page: f.page ?? 1,
          pageSize: f.pageSize ?? 40,
        },
        signal,
      )
    },
    staleTime: 60_000,
    // Without it every sort or page change swapped the table for a loading panel, which took the
    // header - and the focused sort button - out of the page: a keyboard user was dropped back at
    // the top after every sort. The table stays while the next page loads.
    placeholderData: keepPreviousData,
  })
}

/** One device model. */
export function useDevice(tac: MaybeRefOrGetter<string | null>) {
  return useQuery({
    queryKey: ['device', computed(() => toValue(tac))],
    queryFn: ({ signal }) => api.get<DeviceDetail>(`/api/v1/devices/${toValue(tac)}`, undefined, signal),
    enabled: computed(() => isTac(toValue(tac))),
    staleTime: 5 * 60_000,
  })
}

/** One device model's daily movement. */
export function useDeviceTimeline(
  tac: MaybeRefOrGetter<string | null>,
  range: MaybeRefOrGetter<{ from: string | null; to: string | null }>,
) {
  return useQuery({
    queryKey: ['device-timeline', computed(() => toValue(tac)), computed(() => toValue(range))],
    queryFn: ({ signal }) => {
      const r = toValue(range)
      const params: Record<string, string> = {}
      if (r.from) params['from'] = r.from
      if (r.to) params['toDate'] = r.to
      return api.get<DeviceTimelineResponse>(
        `/api/v1/devices/${toValue(tac)}/timeline`,
        params,
        signal,
      )
    },
    enabled: computed(() => isTac(toValue(tac))),
    staleTime: 5 * 60_000,
  })
}

/**
 * A page of one device model's identifiers.
 *
 * A mutation, not a query, and this one genuinely is the IMSI page's reasoning: it is bulk
 * exposure of personal identifiers, it is audited server-side on every call, and it must not
 * re-run on its own when a component remounts or the window regains focus. Caching a page of
 * subscriber numbers would also keep them in memory long after the tab moved on.
 */
export function useDeviceIdentifiers() {
  return useMutation({
    mutationFn: (input: {
      tac: string
      activeOnly?: boolean
      from?: string | null
      to?: string | null
      page?: number
      pageSize?: number
    }) =>
      api.post<DeviceIdentifiersResponse>(`/api/v1/devices/${input.tac}/identifiers`, {
        activeOnly: input.activeOnly ?? false,
        from: input.from || null,
        to: input.to || null,
        page: input.page ?? 1,
        pageSize: input.pageSize ?? 50,
      }),
  })
}

/** Values available to filter the catalogue by. Rarely changes; cached for the session. */
export function useDeviceFacets() {
  return useQuery({
    queryKey: ['device-facets'],
    queryFn: ({ signal }) => api.get<DeviceFacets>('/api/v1/devices/facets', undefined, signal),
    staleTime: 30 * 60_000,
  })
}

/** True for a well-formed 8-digit type allocation code. */
export function isTac(value: string | null | undefined): value is string {
  return typeof value === 'string' && /^\d{8}$/.test(value)
}

/**
 * Classifies a search term in the browser, with the same rule the server applies.
 *
 * Duplicated deliberately, exactly as the IMSI page duplicates its own rule: this copy exists to
 * explain what will happen before anything is sent, and the server's exists because it is the only
 * one that decides. Getting this copy wrong shows the wrong hint; it cannot let anything through.
 *
 * The rule is length, and it works only because the four lengths are disjoint in this feed —
 * TAC 8, MSISDN 10, IMEI 14, IMSI 15. That was measured, not assumed: no IMEI in this data is 15
 * or 16 digits, which is what makes 15 unambiguously a SIM.
 */
export function classifyTerm(raw: string): {
  kind: DeviceSearchResolution['kind']
  digits: string
  hint: string | null
} {
  const text = raw.trim()
  if (text.length === 0) return { kind: 'Text', digits: '', hint: null }

  // Anything that is not a digit or the punctuation people write numbers with makes it a name.
  if (!/^[\d\s\-._()/]+$/.test(text)) return { kind: 'Text', digits: '', hint: null }

  const digits = text.replace(/[\s\-._()/]/g, '')

  switch (digits.length) {
    case 8:
      return { kind: 'Tac', digits, hint: 'An 8-digit TAC — a device model.' }
    case 10:
      return { kind: 'Msisdn', digits, hint: 'A subscriber number. Shows the handsets bound to it.' }
    case 14:
      return { kind: 'Imei', digits, hint: 'An IMEI — one handset. Its first 8 digits are its model.' }
    case 15:
      return {
        kind: 'Imsi',
        digits,
        hint: digits.startsWith(IMSI_CONSTANT_PREFIX)
          ? 'An IMSI — one SIM. Shows the handsets it has been in.'
          : `15 digits, so it is read as an IMSI — though every IMSI here starts ${IMSI_CONSTANT_PREFIX}.`,
      }
    default:
      if (digits === '000000') {
        return {
          kind: 'UnknownDeviceSentinel',
          digits,
          hint: 'The source’s marker for “device not known”, not a handset.',
        }
      }
      return {
        kind: 'Text',
        digits: '',
        hint: `${digits.length} digits matches no identifier here — searched as text instead.`,
      }
  }
}
