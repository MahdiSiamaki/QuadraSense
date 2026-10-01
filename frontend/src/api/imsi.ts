import { useMutation, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/*
  IMSI search.

  Both calls are POSTs with the identifier in the body, never a query string. A GET would put a
  real SIM identity into web-server access logs, browser history and any Referer the page emits -
  and no amount of masking in the UI undoes that, because the value is already in three logs by
  the time the page is drawn.
*/

/** The measured minimum. Every IMSI in this feed starts 43211, so a shorter prefix matches all. */
export const MIN_IMSI_PREFIX = 10

/** Digits shared by every IMSI in the dataset: MCC 432, MNC 11. */
export const IMSI_CONSTANT_PREFIX = '43211'

/** The expected length of a complete IMSI. */
export const IMSI_LENGTH = 15

export interface ImsiMatch {
  imsi: string
  msisdn: string
  imei: string
  /** The device model's type allocation code. Never masked: it identifies a model, not a person. */
  tac: string | null
  manufacturer: string | null
  marketingName: string | null
  deviceType: string | null
  operatingSystem: string | null
  isActive: boolean
  /**
   * The day a daily file last said anything about this binding.
   *
   * Null means no daily file ever has: the binding exists because the initial dump listed it, and
   * nothing has confirmed or removed it since.
   */
  lastChangeDate: string | null
}

export interface ImsiSummary {
  /** More than one means the SIM has been moved between numbers. */
  distinctSubscribers: number
  /** More than one means the SIM has been moved between handsets. */
  distinctHandsets: number
  activeBindings: number
  firstSeen: string | null
  lastSeen: string | null
  /** False when every binding is still at sequence 0 - dump-only, never confirmed. */
  everTouchedByDailyFile: boolean
  /**
   * firstSeen stands for the initial dump's window, not a known day. First and last seen come from
   * the binding history once it is complete (corrected 2026-10-01: they were the earliest and
   * latest last-change dates).
   */
  firstSeenIsDumpWindow: boolean
}

export interface SearchTiming {
  elapsedMs: number
  /** Rows the engine read. The number that tells an indexed lookup from a scan. */
  rowsExamined: number
}

export interface ImsiSearchResponse {
  term: string
  isExact: boolean
  /** False for the 16- and 17-digit anomalies, which stay searchable so they can be investigated. */
  wellFormed: boolean
  total: number
  page: number
  pageSize: number
  /** Whether the server sent complete identifiers or redacted ones. */
  identifiers: 'Masked' | 'Full'
  items: ImsiMatch[]
  summary: ImsiSummary | null
  timing: SearchTiming
}

export interface ImsiHistoryEvent {
  date: string
  sequence: number
  msisdn: string
  imei: string
  manufacturer: string | null
  marketingName: string | null
  added: boolean
}

export interface ImsiHistoryResponse {
  imsi: string
  from: string | null
  to: string | null
  events: ImsiHistoryEvent[]
  truncated: boolean
  timing: SearchTiming
}

export interface ImsiSearchFilters {
  imsi: string
  deviceType?: string | null
  manufacturer?: string | null
  activeOnly?: boolean
  from?: string | null
  to?: string | null
  page?: number
  pageSize?: number
}

/**
 * Runs a search.
 *
 * A mutation rather than a query even though it reads nothing: it is a POST the user triggers
 * deliberately, it is audited server-side, and it must not re-run on its own when a component
 * remounts or the window regains focus. Caching a search whose input is a person's SIM identity
 * would also keep that identity in memory long after the tab moved on.
 */
export function useImsiSearch() {
  return useMutation({
    mutationFn: (filters: ImsiSearchFilters) =>
      api.post<ImsiSearchResponse>('/api/v1/lookup/imsi/search', {
        imsi: filters.imsi,
        deviceType: filters.deviceType || null,
        manufacturer: filters.manufacturer || null,
        activeOnly: filters.activeOnly ?? false,
        from: filters.from || null,
        to: filters.to || null,
        page: filters.page ?? 1,
        pageSize: filters.pageSize ?? 50,
      }),
  })
}

/** One SIM's dated history. Enabled only once an exact IMSI has been opened. */
export function useImsiHistory(
  imsi: MaybeRefOrGetter<string | null>,
  range: MaybeRefOrGetter<{ from: string | null; to: string | null }>,
) {
  return useQuery({
    queryKey: ['imsi-history', computed(() => toValue(imsi)), computed(() => toValue(range))],
    queryFn: ({ signal }) =>
      api.post<ImsiHistoryResponse>(
        '/api/v1/lookup/imsi/history',
        { imsi: toValue(imsi), ...toValue(range) },
        signal,
      ),
    enabled: computed(() => {
      const value = toValue(imsi)
      return value !== null && value.length >= IMSI_LENGTH
    }),
    staleTime: 60_000,
  })
}

/**
 * Checks a term in the browser, with the same rule the server applies.
 *
 * Duplicated deliberately: this one exists to explain the rule before the user presses anything,
 * and the server's exists because it is the only one that decides. Getting this copy wrong shows
 * a message too early or too late; it cannot let a bad term through.
 */
export function describeTerm(raw: string): {
  digits: string
  ok: boolean
  isExact: boolean
  problem: string | null
} {
  const digits = raw.replace(/[\s\-._()]/g, '')

  if (digits.length === 0) {
    return { digits, ok: false, isExact: false, problem: null }
  }

  if (!/^\d+$/.test(digits)) {
    return { digits, ok: false, isExact: false, problem: 'An IMSI is digits only.' }
  }

  if (digits.length > 17) {
    return {
      digits,
      ok: false,
      isExact: false,
      problem: `That is ${digits.length} digits. An IMSI is ${IMSI_LENGTH}.`,
    }
  }

  if (digits.length < MIN_IMSI_PREFIX) {
    return {
      digits,
      ok: false,
      isExact: false,
      problem: digits.startsWith(IMSI_CONSTANT_PREFIX)
        ? `Every IMSI here starts ${IMSI_CONSTANT_PREFIX}, so ${digits.length} digits matches the whole network. Enter at least ${MIN_IMSI_PREFIX}.`
        : `Enter at least ${MIN_IMSI_PREFIX} digits to search by prefix.`,
    }
  }

  return { digits, ok: true, isExact: digits.length >= IMSI_LENGTH, problem: null }
}

/**
 * Roughly how many IMSIs a prefix covers.
 *
 * Shown beside the input so the cost of a short prefix is visible before it is paid. Ten digits
 * leaves five unspecified - a hundred thousand possible SIMs, of which the worst measured bucket
 * held 557,158 bindings.
 */
export function prefixBreadth(digits: number): number {
  return digits >= IMSI_LENGTH ? 1 : 10 ** (IMSI_LENGTH - digits)
}
