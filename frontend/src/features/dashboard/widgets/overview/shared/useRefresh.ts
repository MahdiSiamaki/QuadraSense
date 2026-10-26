/**
 * The toolbar's Refresh, for aggregate widgets: when the dashboard bumps `refreshToken`, each source
 * that is allowed to run fetches again.
 *
 * Only enabled sources: TanStack's `refetch` ignores `enabled`, and a feed-quality query refetched
 * for someone without import.view is a 403 in the audit log. `cancelRefetch: false` joins a request
 * already in flight, so six KPI cards sharing one /kpi query send it once.
 */
import { inject, watch } from 'vue'
import { DASHBOARD_CONTEXT } from '@/features/dashboard/types/widget'

export interface RefreshSource {
  enabled: () => boolean
  refetch: (options: { cancelRefetch: boolean }) => unknown
}

export function useRefresh(sources: RefreshSource[]): void {
  const context = inject(DASHBOARD_CONTEXT, null)
  if (!context) return
  watch(context.refreshToken, () => {
    for (const source of sources) {
      if (source.enabled()) void source.refetch({ cancelRefetch: false })
    }
  })
}
