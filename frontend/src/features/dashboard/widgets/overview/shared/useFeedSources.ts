/**
 * The two optional sources the daily charts read beside their own series, both behind import.view.
 *
 * - **Flagged days** (`/quality/days`): days whose file looked unlike the ordinary days - shifted
 *   IMEIs, SIMs with several numbers - shaded on the chart and named in its tooltip.
 * - **Missing days** (`/imports/freshness`): the days the source never delivered, from the platform's
 *   own calendar of expected days (imports.expected_business_date), so the footnote cannot drift
 *   away from the chart the way a hard-coded list once could.
 *
 * Without import.view neither query runs - no 403 in the audit log - and the chart is drawn without
 * shading, as the dashboard page always did. An export carries the same two sources, null when the
 * person may not read them.
 */
import { computed, toValue, type ComputedRef, type MaybeRefOrGetter } from 'vue'
import { flaggedDays, useFeedQuality, type FeedQualityResponse } from '@/api/quality'
import type { SourceFreshness } from '@/api/imports'
import { Permission, useAuth } from '@/features/auth/useAuth'
import { useFreshness } from '@/features/imports/useImportQueries'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { RefreshSource } from './useRefresh'

interface FeedSnapshot {
  feedQuality: FeedQualityResponse | null
  freshness?: SourceFreshness[] | null
}

export interface FeedSources {
  /** Flagged date to a short reason; empty when shading is off, not permitted or not loaded. */
  flagged: ComputedRef<ReadonlyMap<string, string>>
  /**
   * The source's missing days, ascending; null when not known here (no import.view, not asked for,
   * not loaded) - the caller then falls back to the holes in its own series.
   */
  missingDays: ComputedRef<string[] | null>
  refreshSources: RefreshSource[]
}

export function useFeedSources(
  props: WidgetViewProps<any>,
  options: {
    flagged: MaybeRefOrGetter<boolean>
    missing: MaybeRefOrGetter<boolean>
    snapshot: (raw: unknown) => FeedSnapshot
  },
): FeedSources {
  const { can } = useAuth()
  const active = useQueryActive(props)
  const canSeeImports = computed(() => can(Permission.ImportView))
  const wantFlagged = computed(() => toValue(options.flagged))
  const wantMissing = computed(() => toValue(options.missing))

  const feedEnabled = computed(() => active.value && canSeeImports.value && wantFlagged.value)
  const freshnessEnabled = computed(() => active.value && canSeeImports.value && wantMissing.value)

  const feedQuery = useFeedQuality({ enabled: feedEnabled })
  const freshnessQuery = useFreshness({ enabled: freshnessEnabled })

  const feed = useWidgetData(props, feedQuery, (raw) => options.snapshot(raw).feedQuality ?? undefined)
  const freshness = useWidgetData(props, freshnessQuery, (raw) => options.snapshot(raw).freshness ?? undefined)

  const flagged = computed<ReadonlyMap<string, string>>(() =>
    wantFlagged.value ? flaggedDays(feed.data.value) : new Map<string, string>(),
  )

  const missingDays = computed(() => {
    if (!wantMissing.value) return null
    const list = freshness.data.value?.find((f) => f.sourceCode === 'SQM')?.missingBusinessDates
    return list ? [...list].sort() : null
  })

  return {
    flagged,
    missingDays,
    refreshSources: [
      { enabled: () => feedEnabled.value, refetch: feedQuery.refetch },
      { enabled: () => freshnessEnabled.value, refetch: freshnessQuery.refetch },
    ],
  }
}
