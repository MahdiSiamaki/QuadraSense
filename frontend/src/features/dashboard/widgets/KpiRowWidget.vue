<script setup lang="ts">
import { computed } from 'vue'
import { useKpiSummary, type DashboardFilters } from '@/api/dashboard'
import KpiCard from '@/design-system/KpiCard.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { formatPercent } from '@/lib/format'

const props = defineProps<{ filters: DashboardFilters }>()

const kpi = useKpiSummary(() => props.filters)

const unknownShare = computed(() => {
  const k = kpi.data.value
  if (!k?.activeBindings) return null
  return (k.unknownDeviceBindings / k.activeBindings) * 100
})
</script>

<template>
  <AsyncBoundary
    :is-loading="kpi.isPending.value"
    :is-error="kpi.isError.value"
    :error="kpi.error.value"
    min-height="5.5rem"
    @retry="kpi.refetch()"
  >
    <template #skeleton>
      <div class="grid grid-cols-2 gap-3 lg:grid-cols-5">
        <div v-for="i in 5" :key="i" class="h-[5.5rem] rounded-[var(--radius-lg)] bg-[var(--c-surface-sunken)]" />
      </div>
    </template>

    <div class="grid grid-cols-2 gap-3 lg:grid-cols-5">
      <KpiCard label="Active bindings" :value="kpi.data.value?.activeBindings" />
      <KpiCard label="Subscribers" :value="kpi.data.value?.distinctSubscribers" hint="approx." />
      <KpiCard label="Devices" :value="kpi.data.value?.distinctDevices" hint="approx." />
      <KpiCard
        label="Unknown device"
        :value="kpi.data.value?.unknownDeviceBindings"
        tone="warning"
        :hint="unknownShare ? `${formatPercent(unknownShare)} of bindings` : undefined"
      />
      <KpiCard
        label="TAC coverage"
        :value="kpi.data.value?.tacCoveragePercent"
        percent
        hint="enriched from GSMA"
      />
    </div>
  </AsyncBoundary>
</template>
