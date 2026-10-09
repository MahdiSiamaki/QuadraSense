<script setup lang="ts">
import { computed } from 'vue'
import { RULE_TITLES, useRiskDistribution } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import RiskDistributionChart from '@/features/risk/RiskDistributionChart.vue'
import { useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate } from '@/lib/format'
import type { RiskDistributionConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { useKept } from '../overview/shared/useKept'

/**
 * Where a rule's threshold sits: every stored entity by its value, on a log scale, with the listed
 * range shaded. The risk page's own chart. Names nobody.
 */
const props = defineProps<WidgetViewProps<RiskDistributionConfig>>()
type Snapshot = SnapshotData<'risk.distribution'>

const { active, status, ready } = useRiskSource(props)
const query = useRiskDistribution(() => props.instance.config.rule, { enabled: () => active.value && ready.value })
const distribution = useWidgetData(props, query, (raw) => (raw as Snapshot).distribution ?? undefined)
const { value: data, loading, updating } = useKept(distribution)

const rule = computed(() => status.data.value?.rules.find((r) => r.rule === props.instance.config.rule) ?? null)
const hasRaw = computed(() => data.value?.buckets.some((b) => b.raw !== null) ?? false)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      {{ RULE_TITLES[instance.config.rule] ?? instance.config.rule }}: every stored entity by its count<template v-if="data">,
      as of {{ formatDate(data.asOf) }}</template>; values under {{ data?.floor ?? rule?.floor ?? '…' }} are not stored.
    </p>
    <RiskNotice :status="status.data.value" />
    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value || loading"
      :is-error="status.isError.value || distribution.isError.value"
      :error="status.error.value ?? distribution.error.value"
      min-height="10rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : distribution.refetch()"
    >
      <div v-if="data" class="min-h-0 flex-1 overflow-auto">
        <RiskDistributionChart :data="data" :values="instance.config.values" :show-threshold="instance.config.showThreshold" height="14rem" />
      </div>
    </AsyncBoundary>
    <p v-if="data && instance.config.values !== 'clean' && !hasRaw" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      This rule has no count before the screens, so only clean counts are drawn.
    </p>
  </div>
</template>
