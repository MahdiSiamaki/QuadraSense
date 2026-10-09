<script setup lang="ts">
import { computed } from 'vue'
import { useDistribution } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { DistributionConfig } from './config'
import { countUnit, countedBy } from '../overview/shared/countBy'
import { useDrill } from '../overview/shared/drill'
import { lensCountBy, lensFilters } from '../overview/shared/filters'
import { useKept } from '../overview/shared/useKept'

/**
 * How the population splits over a low-cardinality dimension: device types or operating systems.
 * Both are lens filters, so a row drills the whole dashboard in.
 */
const props = defineProps<WidgetViewProps<DistributionConfig>>()

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const countBy = computed(() => lensCountBy(props.lens))
const filters = computed(() => lensFilters(props.lens))
const query = useDistribution(() => config.value.dimension, filters, countBy, { enabled: active })
const data = useWidgetData(props, query, (raw) => (raw as SnapshotData<'device.distribution'>).rows)
const { value: rows, loading, updating } = useKept(data)

const shown = computed(() => (rows.value ?? []).slice(0, config.value.limit))

const { canDrill, drill } = useDrill(() => props.mode)
const drillable = computed(() => canDrill(config.value.clickToFilter))

const caption = computed(() => {
  const parts = [countedBy(countBy.value)]
  if (config.value.dimension === 'operatingSystem') parts.push('Normalised for case and whitespace.')
  if (drillable.value) parts.push('Click to filter.')
  return parts.join(' ')
})

const header = computed(() => (config.value.dimension === 'deviceType' ? 'Device type' : 'Operating system'))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">{{ caption }}</p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="data.isError.value"
      :error="data.error.value"
      :is-empty="!loading && shown.length === 0"
      min-height="10rem"
      gap="none"
      @retry="data.refetch()"
    >
      <div v-if="config.display === 'table'" class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <DimensionTable
          :rows="shown"
          :header="header"
          :unit="countUnit(countBy)"
          :show-share="config.showPercent"
          :selectable="drillable"
          @select="(v) => drill(config.dimension, v, config.clickToFilter)"
        />
      </div>
      <div v-else class="relative min-h-0 flex-1">
        <BarChart
          :data="shown"
          :unit="countUnit(countBy)"
          height="100%"
          :show-percent="config.showPercent"
          :selectable="drillable"
          @select="(v) => drill(config.dimension, v, config.clickToFilter)"
        />
      </div>
    </AsyncBoundary>
    <p v-if="countBy === 'subscribers'" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      Subscribers overlap across these groups: someone with a phone and a tablet is counted in both, so the shares can
      sum to more than 100%.
    </p>
  </div>
</template>
