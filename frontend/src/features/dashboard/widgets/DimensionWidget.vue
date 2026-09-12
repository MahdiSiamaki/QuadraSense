<script setup lang="ts">
import { computed } from 'vue'
import { useTopDimension, type DashboardFilters, type Dimension } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import type { WidgetConfig } from './registry'
import { DIMENSION_OPTIONS } from './registry'

const props = defineProps<{ config: WidgetConfig; filters: DashboardFilters }>()
const emit = defineEmits<{ drill: [dimension: Dimension, value: string] }>()

const dimension = computed<Dimension>(() => props.config.dimension ?? 'vendorCanonical')
const limit = computed(() => props.config.limit ?? 10)

/**
 * A widget may exclude the unknown-device bucket regardless of the global toggle.
 *
 * The model breakdown sets this by default: at 8.8M the bucket dwarfs every real
 * model and makes the bars incomparable, which is the one thing the chart exists
 * to do. It is not an answer to "which models are popular" — it is the absence of
 * one. The count is still visible in the KPI row and the quality widget.
 */
const effectiveFilters = computed<DashboardFilters>(() =>
  props.config.excludeUnknown
    ? { ...props.filters, includeUnknownDevice: false }
    : props.filters,
)

const query = useTopDimension(dimension, effectiveFilters, limit)

const label = computed(
  () => DIMENSION_OPTIONS.find((o) => o.value === dimension.value)?.label ?? dimension.value,
)

function onSelect(value: string) {
  if (value.startsWith('(unknown')) return
  emit('drill', dimension.value, value)
}
</script>

<template>
  <AsyncBoundary
    :is-loading="query.isPending.value"
    :is-error="query.isError.value"
    :error="query.error.value"
    :is-empty="query.data.value?.length === 0"
    min-height="14rem"
    @retry="query.refetch()"
  >
    <BarChart
      v-if="config.style !== 'table'"
      :data="query.data.value ?? []"
      height="100%"
      @select="onSelect"
    />
    <DimensionTable
      v-else
      :rows="query.data.value ?? []"
      :header="label"
      @select="onSelect"
    />
  </AsyncBoundary>
</template>
