<script setup lang="ts">
import { computed, inject } from 'vue'
import { useDailyChurn } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import ChurnTimeSeries from '@/design-system/ChurnTimeSeries.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import type { SimHandsetConfig } from './config'
import { cutToWindow, lastDate } from '../overview/shared/series'
import { useFeedSources } from '../overview/shared/useFeedSources'
import { useKept } from '../overview/shared/useKept'

/**
 * Numbers that moved to a different SIM or a different handset, per day - the same-day definition,
 * so a floor rather than a total. The two shaded-day caveats name the feed defects that show up here
 * as changes that did not happen.
 */
const props = defineProps<WidgetViewProps<SimHandsetConfig>>()
type Snapshot = SnapshotData<'change.simHandset'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const query = useDailyChurn({ enabled: active })
const churn = useWidgetData(props, query, (raw) => (raw as Snapshot).churn)
const { value: rows, loading, updating } = useKept(churn)

const feed = useFeedSources(props, {
  flagged: () => config.value.showFlagged !== false,
  missing: false,
  snapshot: (raw) => raw as Snapshot,
})

const context = inject(DASHBOARD_CONTEXT, null)
const dataThrough = computed(() => context?.dataThrough.value ?? lastDate(rows.value ?? []))
const window = computed(() => resolveRange(props.lens.range, dataThrough.value))
const shown = computed(() => cutToWindow(rows.value ?? [], window.value))
const whole = computed(() => window.value.from === null && window.value.to === null)
const flaggedShown = computed(() => shown.value.some((r) => feed.flagged.value.has(r.date)))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Subscribers who moved to a different SIM or a different device, per day.
    </p>

    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="churn.isError.value"
      :error="churn.error.value"
      :is-empty="!loading && shown.length === 0"
      :empty-message="(rows?.length ?? 0) === 0 ? 'No daily files loaded yet.' : 'No delivered day in this date range.'"
      min-height="12rem"
      gap="none"
      @retry="churn.refetch()"
    >
      <div class="relative min-h-0 flex-1 transition-opacity duration-150" :class="updating ? 'opacity-70' : ''">
        <ChurnTimeSeries
          :data="shown"
          :flagged="feed.flagged.value"
          :series="config.series"
          :chart="config.chart"
          :zoom-start="whole ? 66 : 0"
          height="100%"
        />
      </div>
    </AsyncBoundary>

    <div class="shrink-0 space-y-1.5 text-2xs text-[var(--c-text-muted)]">
      <p>
        Same-day definition: the number has a remove carrying one SIM (or handset) and an add carrying another on the
        same date. A change spanning midnight is not counted, so these are a floor, not a total.
      </p>
      <p v-if="flaggedShown">
        Shaded days: the feed itself looked wrong. From 2026-07-27 SIMs began carrying several numbers a day, and from
        2026-09-15 IMEIs arrived shifted by a digit; both show here as SIM and handset changes that did not happen.
      </p>
    </div>
  </div>
</template>
