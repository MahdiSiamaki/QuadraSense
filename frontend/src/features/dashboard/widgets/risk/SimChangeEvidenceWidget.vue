<script setup lang="ts">
import { computed, inject } from 'vue'
import { useRiskDaily, type RiskChangeDay } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import RiskDailyChart from '@/features/risk/RiskDailyChart.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import type { SimChangeEvidenceConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { cutToWindow, lastDate } from '../overview/shared/series'
import { useKept } from '../overview/shared/useKept'

/**
 * Numbers whose SIM changed, per day: what the risk lists count, and what the screens set aside
 * because the feed listed one of their SIMs under several numbers. The evidence behind the
 * SIM-change rule, in the dashboard's date range. Names nobody.
 */
const props = defineProps<WidgetViewProps<SimChangeEvidenceConfig>>()
type Snapshot = SnapshotData<'risk.simChangeEvidence'>

const { active, status, ready } = useRiskSource(props)
const query = useRiskDaily(() => active.value && ready.value)
const daily = useWidgetData(props, query, (raw) => (raw as Snapshot).daily ?? undefined)
const { value: data, loading, updating } = useKept(daily)

const context = inject(DASHBOARD_CONTEXT, null)
const days = computed<RiskChangeDay[]>(() => data.value?.days ?? [])
const window = computed(() => resolveRange(props.lens.range, context?.dataThrough.value ?? lastDate(days.value)))
const shown = computed(() => cutToWindow(days.value, window.value))
const whole = computed(() => window.value.from === null && window.value.to === null)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Numbers whose SIM changed, by day: counted, and set aside because the feed listed one of their SIMs under several
      numbers.<template v-if="instance.config.showFlagged"> Shaded days were flagged by the feed-quality monitor.</template>
    </p>
    <RiskNotice :status="status.data.value" />
    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value || loading"
      :is-error="status.isError.value || daily.isError.value"
      :error="status.error.value ?? daily.error.value"
      :is-empty="!loading && shown.length === 0"
      :empty-message="days.length === 0 ? 'No SIM changes recorded yet.' : 'No day in this date range.'"
      min-height="10rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : daily.refetch()"
    >
      <div class="relative min-h-0 flex-1">
        <RiskDailyChart
          :days="shown"
          :series="instance.config.series"
          :show-flagged="instance.config.showFlagged"
          :zoom-start="whole ? 50 : 0"
          height="100%"
        />
      </div>
    </AsyncBoundary>
  </div>
</template>
