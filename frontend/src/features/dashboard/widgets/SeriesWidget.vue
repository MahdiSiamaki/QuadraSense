<script setup lang="ts">
import { computed } from 'vue'
import { useSeries } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import CompositionBar from '@/design-system/CompositionBar.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import type { WidgetConfig, SeriesName } from './registry'
import { SERIES_OPTIONS } from './registry'

const props = defineProps<{ config: WidgetConfig }>()

const series = computed<SeriesName>(() => props.config.series ?? 'deviceClass')
const query = useSeries(series)

const label = computed(
  () => SERIES_OPTIONS.find((o) => o.value === series.value)?.label ?? series.value,
)

/**
 * The composition bar is only correct for series whose parts sum to a meaningful whole.
 *
 * Device class mix does: every binding falls in exactly one class, so the bar shows
 * how the population divides. Devices-per-subscriber does not — its parts are counts of
 * *subscribers*, not of bindings, so rendering them as shares of one bar would invite a
 * reader to compare quantities that are not parts of the same total. Those fall back to
 * bars, where each value is read on its own.
 */
const canUseComposition = computed(() => series.value === 'deviceClass')

const style = computed(() => {
  if (props.config.style === 'composition' && !canUseComposition.value) return 'bar'
  return props.config.style ?? 'composition'
})

/** Horizontal for named categories, vertical for the ordered 1..10+ buckets. */
const horizontal = computed(
  () => series.value !== 'devicesPerSubscriber' && series.value !== 'subscribersPerDevice',
)
</script>

<template>
  <AsyncBoundary
    :is-loading="query.isPending.value"
    :is-error="query.isError.value"
    :error="query.error.value"
    :is-empty="query.data.value?.length === 0"
    min-height="8rem"
    @retry="query.refetch()"
  >
    <CompositionBar v-if="style === 'composition'" :data="query.data.value ?? []" />
    <DimensionTable
      v-else-if="style === 'table'"
      :rows="query.data.value ?? []"
      :header="label"
    />
    <BarChart v-else :data="query.data.value ?? []" :horizontal="horizontal" height="100%" />
  </AsyncBoundary>
</template>
