import type { QueryClient } from '@tanstack/vue-query'
import type { Ref } from 'vue'

/** The aggregate queries a refresh may repeat. Audited lists and queries are mutations: never these. */
export const REFRESHABLE = new Set([
  'kpi', 'top', 'distribution', 'device-class-mix', 'capabilities', 'daily-changes', 'daily-churn', 'vendor-movement',
  'feed-quality', 'quality-signals', 'model-arrivals', 'risk', 'imports', 'tac-versions', 'device',
])

/**
 * The toolbar's Refresh: the widgets that watch the token fetch again (useRefresh), and every
 * refreshable query is marked stale and fetched again - each once.
 *
 * `cancelRefetch: false`, so the invalidation joins a request already in flight. Vue Query runs an
 * invalidation's refetch on the next tick, after the widgets have answered the token, and by default
 * it cancels the request it finds running and sends it again: on 2026-10-19 every Refresh sent the
 * filtered /kpi twice, the first aborted 8 ms in - and on a filter that takes seconds, the aborted
 * query is work the server had already started.
 */
export function refreshDashboard(queryClient: QueryClient, token: Ref<number>): Promise<void> {
  token.value += 1
  return queryClient.invalidateQueries(
    { predicate: (q) => REFRESHABLE.has(String(q.queryKey[0])) },
    { cancelRefetch: false },
  )
}
