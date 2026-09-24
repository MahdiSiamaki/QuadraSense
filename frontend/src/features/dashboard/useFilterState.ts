import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { DashboardFilters } from '@/api/dashboard'

/**
 * Dashboard filters, stored in the URL rather than component state.
 *
 * The URL is the source of truth, which buys three things for free: a filtered
 * view is shareable by copying the address bar, the browser back button undoes a
 * drill-down, and a reload restores exactly what the user was looking at.
 *
 * Only known keys are read, and each is validated, so a hand-edited or truncated
 * URL degrades to "no filter" instead of throwing.
 */

const FILTER_KEYS = ['vendor', 'deviceType', 'operatingSystem', 'tac'] as const
type FilterKey = (typeof FILTER_KEYS)[number]

/** What breakdowns count. Held in the URL so a shared link carries it. */
export type CountBy = 'bindings' | 'subscribers' | 'handsets'
const COUNT_BY_VALUES: CountBy[] = ['bindings', 'subscribers', 'handsets']

/** Rejects absurd input before it reaches a query string or an API call. */
const MAX_VALUE_LENGTH = 120

function readString(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const trimmed = value.trim()
  if (!trimmed || trimmed.length > MAX_VALUE_LENGTH) return undefined
  return trimmed
}

export function useFilterState() {
  const route = useRoute()
  const router = useRouter()

  // Defaults to bindings, the historical behaviour, so existing links keep meaning
  // what they meant. An unrecognised value degrades to the default rather than throwing.
  const countBy = computed<CountBy>(() => {
    const raw = route.query['countBy']
    return typeof raw === 'string' && (COUNT_BY_VALUES as string[]).includes(raw)
      ? (raw as CountBy)
      : 'bindings'
  })

  function setCountBy(next: CountBy) {
    const query = { ...route.query }
    if (next === 'bindings') delete query['countBy']
    else query['countBy'] = next
    // replace, not push: switching the unit is a lens change, not a navigation step.
    void router.replace({ query })
  }

  const filters = computed<DashboardFilters>(() => {
    const result: DashboardFilters = { includeUnknownDevice: route.query['hideUnknown'] !== '1' }
    for (const key of FILTER_KEYS) {
      const value = readString(route.query[key])
      if (value) result[key] = value
    }
    return result
  })

  const activeFilters = computed(() =>
    FILTER_KEYS.map((key) => ({ key, value: filters.value[key] })).filter(
      (f): f is { key: FilterKey; value: string } => Boolean(f.value),
    ),
  )

  function setFilter(key: FilterKey, value: string | undefined) {
    const query = { ...route.query }
    if (value) query[key] = value
    else delete query[key]
    // push, not replace: drilling in should be undoable with the back button.
    void router.push({ query })
  }

  function toggleUnknownDevice() {
    const query = { ...route.query }
    if (filters.value.includeUnknownDevice) query['hideUnknown'] = '1'
    else delete query['hideUnknown']
    void router.replace({ query })
  }

  // Clears the chips it sits beside, not the view. It emptied the whole query, so someone counting
  // by handsets with unknown devices hidden was put back on bindings with them shown - two
  // settings this file itself treats as a lens (replace, not push), not as filters.
  function clearAll() {
    const query = { ...route.query }
    for (const key of FILTER_KEYS) delete query[key]
    void router.push({ query })
  }

  return { filters, activeFilters, setFilter, toggleUnknownDevice, clearAll, countBy, setCountBy }
}

/** Human-readable label for a filter chip. */
export const FILTER_LABELS: Record<FilterKey, string> = {
  vendor: 'Vendor',
  deviceType: 'Device type',
  operatingSystem: 'OS',
  tac: 'TAC',
}
