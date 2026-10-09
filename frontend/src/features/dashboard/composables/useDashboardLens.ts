/**
 * The view a dashboard is looked at through, held in the URL (ADR-016 §8).
 *
 * The same keys and the same validation `useFilterState` has always used - vendor, deviceType,
 * operatingSystem, tac, countBy, hideUnknown=1 - so every old link to `/` still means what it meant,
 * plus `range`. The URL is the transient view: reload and Back reproduce it. A dashboard's saved
 * defaults are a separate thing, stored in its definition and changed only in edit mode.
 *
 * A hand-edited or truncated URL degrades to "no filter" instead of throwing. Never an MSISDN, IMSI
 * or IMEI: a TAC names a device model, not a person.
 */
import { computed } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import type { CountBy, DashboardLens } from '../types/definition'
import { isValidRange, sanitizeLens } from './lens'

export const LENS_FILTER_KEYS = ['vendor', 'deviceType', 'operatingSystem', 'tac'] as const
export type LensFilterKey = (typeof LENS_FILTER_KEYS)[number]

export const LENS_FILTER_LABELS: Record<LensFilterKey, string> = {
  vendor: 'Vendor',
  deviceType: 'Device type',
  operatingSystem: 'OS',
  tac: 'TAC',
}

const MAX_VALUE_LENGTH = 120

function readString(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const trimmed = value.trim()
  if (!trimmed || trimmed.length > MAX_VALUE_LENGTH) return undefined
  return trimmed
}

/** Only what the URL names, already validated. */
export function lensFromQuery(query: LocationQuery): DashboardLens {
  const raw: DashboardLens = {}
  for (const key of LENS_FILTER_KEYS) {
    const value = readString(query[key])
    if (value) raw[key] = value
  }
  const countBy = query['countBy']
  if (typeof countBy === 'string') raw.countBy = countBy as CountBy
  // hideUnknown=1 is the old page's switch; =0 is new, for a dashboard whose default hides them.
  if (query['hideUnknown'] === '1') raw.includeUnknownDevice = false
  else if (query['hideUnknown'] === '0') raw.includeUnknownDevice = true
  const range = query['range']
  if (typeof range === 'string' && isValidRange(range)) raw.range = range
  return sanitizeLens(raw)
}

export function useDashboardLens() {
  const route = useRoute()
  const router = useRouter()

  /** What the URL says, nothing more. Defaults are applied by the page, per widget. */
  const urlLens = computed(() => lensFromQuery(route.query))

  /** push, not replace: drilling in should be undoable with Back. */
  function setFilter(key: LensFilterKey, value: string | undefined) {
    const query = { ...route.query }
    if (value) query[key] = value
    else delete query[key]
    void router.push({ query })
  }

  /** Clears the filter chips, not the view: count unit, unknown devices and range are a lens, kept. */
  function clearFilters() {
    const query = { ...route.query }
    for (const key of LENS_FILTER_KEYS) delete query[key]
    void router.push({ query })
  }

  /** replace, not push: switching the unit is a lens change, not a navigation step. */
  function setCountBy(next: CountBy, dashboardDefault: CountBy = 'bindings') {
    const query = { ...route.query }
    if (next === dashboardDefault) delete query['countBy']
    else query['countBy'] = next
    void router.replace({ query })
  }

  function setIncludeUnknown(include: boolean, dashboardDefault = true) {
    const query = { ...route.query }
    if (include === dashboardDefault) delete query['hideUnknown']
    else query['hideUnknown'] = include ? '0' : '1'
    void router.replace({ query })
  }

  function setRange(range: string, dashboardDefault = 'all') {
    const query = { ...route.query }
    if (range === dashboardDefault) delete query['range']
    else query['range'] = range
    void router.replace({ query })
  }

  return { urlLens, setFilter, clearFilters, setCountBy, setIncludeUnknown, setRange }
}
