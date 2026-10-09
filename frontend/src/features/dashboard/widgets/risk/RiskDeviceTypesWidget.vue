<script setup lang="ts">
import { computed } from 'vue'
import { RULE_TITLES, useRiskDeviceTypes } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { RiskDeviceTypesConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { useKept } from '../overview/shared/useKept'

/**
 * What a rule's list is made of, by GSMA device type: of the handset for IMEI rules, of the SIM's
 * most frequent TAC for SIM rules. Counts at the configured threshold; names nobody.
 */
const props = defineProps<WidgetViewProps<RiskDeviceTypesConfig>>()
type Snapshot = SnapshotData<'risk.deviceTypes'>

const { active, status, ready } = useRiskSource(props)
const query = useRiskDeviceTypes(() => props.instance.config.rule, { enabled: () => active.value && ready.value })
const types = useWidgetData(props, query, (raw) => (raw as Snapshot).deviceTypes ?? undefined)
const { value: data, loading, updating } = useKept(types)

const unit = computed(() => (status.data.value?.rules.find((r) => r.rule === props.instance.config.rule)?.family === 'Sim' ? 'SIMs' : 'IMEIs'))

/** Shares of everything listed, before the limit cuts the tail. */
const rows = computed(() => {
  const all = data.value?.rows ?? []
  const total = all.reduce((sum, r) => sum + r.entities, 0) || 1
  return all.slice(0, props.instance.config.limit).map((r) => ({ key: r.deviceType, count: r.entities, percent: (100 * r.entities) / total }))
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      {{ RULE_TITLES[instance.config.rule] ?? instance.config.rule }}: listed at the configured threshold, by device type of
      the {{ data?.basis ?? 'handset' }}.
    </p>
    <RiskNotice :status="status.data.value" />
    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value || loading"
      :is-error="status.isError.value || types.isError.value"
      :error="status.error.value ?? types.error.value"
      :is-empty="!loading && rows.length === 0"
      empty-message="Nothing is listed for this rule."
      min-height="8rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : types.refetch()"
    >
      <div v-if="instance.config.display === 'bar'" class="relative min-h-0 flex-1">
        <BarChart :data="rows" :unit="unit" height="100%" show-percent :selectable="false" />
      </div>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <DimensionTable :rows="rows" header="Device type" :unit="unit" :selectable="false" />
      </div>
    </AsyncBoundary>
  </div>
</template>
