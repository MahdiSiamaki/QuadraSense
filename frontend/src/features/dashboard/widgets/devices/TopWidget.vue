<script setup lang="ts">
import { computed } from 'vue'
import { useTopDimension, type Dimension } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { TopConfig } from './config'
import { countUnit, countedBy } from '../overview/shared/countBy'
import { useDrill, type DrillKey } from '../overview/shared/drill'
import { lensCountBy, lensFilters } from '../overview/shared/filters'
import { useKept } from '../overview/shared/useKept'

/**
 * The largest values of a device dimension: vendors, manufacturers, models or TACs.
 *
 * `excludeUnknownBucket` leaves out the unknown-device bucket whatever the dashboard's view, and the
 * Top models preset turns it on. Not a cosmetic choice: "(unknown device)" is 8.8M bindings - larger
 * than every real model - so including it dwarfs the bars and makes the models incomparable, which is
 * the one thing that chart exists to do. And it is not an answer to "which models are popular": it is
 * the absence of one. The count is not hidden: it stays in the KPIs, the class mix and the device-type
 * table, where it is a category.
 */
const props = defineProps<WidgetViewProps<TopConfig>>()

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const countBy = computed(() => lensCountBy(props.lens))
const filters = computed(() => ({
  ...lensFilters(props.lens),
  ...(config.value.excludeUnknownBucket ? { includeUnknownDevice: false } : {}),
}))
const query = useTopDimension(() => config.value.dimension as Dimension, filters, () => config.value.limit, countBy, { enabled: active })
const data = useWidgetData(props, query, (raw) => (raw as SnapshotData<'device.top'>).rows)
const { value: rows, loading, updating } = useKept(data)

const sorted = computed(() => {
  const list = [...(rows.value ?? [])]
  return config.value.sort === 'name' ? list.sort((a, b) => a.key.localeCompare(b.key)) : list
})

/** A lens filter exists for vendors and TACs; manufacturers and models have none to drill into. */
const DRILL: Record<TopConfig['dimension'], DrillKey | null> = {
  vendorCanonical: 'vendor',
  manufacturer: null,
  marketingName: null,
  tac: 'tac',
}

const { canDrill, drill } = useDrill(() => props.mode)
const drillable = computed(() => DRILL[config.value.dimension] !== null && canDrill(config.value.clickToFilter))

const caption = computed(() => {
  const parts = [countedBy(countBy.value)]
  if (config.value.excludeUnknownBucket) parts.push('Excludes unknown devices.')
  if (drillable.value) parts.push(config.value.display === 'bar' ? 'Click a bar to filter.' : 'Click a row to filter.')
  return parts.join(' ')
})

const HEADERS: Record<TopConfig['dimension'], string> = {
  vendorCanonical: 'Vendor',
  manufacturer: 'Manufacturer',
  marketingName: 'Model',
  tac: 'TAC',
}

const isVendorLike = computed(() => config.value.dimension === 'vendorCanonical' || config.value.dimension === 'manufacturer')

function select(value: string) {
  drill(DRILL[config.value.dimension], value, config.value.clickToFilter)
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">{{ caption }}</p>

    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="data.isError.value"
      :error="data.error.value"
      :is-empty="!loading && sorted.length === 0"
      min-height="10rem"
      gap="none"
      @retry="data.refetch()"
    >
      <div v-if="config.display === 'bar'" class="relative min-h-0 flex-1">
        <BarChart
          :data="sorted"
          :unit="countUnit(countBy)"
          height="100%"
          :show-values="config.showValues"
          :show-percent="config.showPercent"
          :selectable="drillable"
          @select="select"
        />
      </div>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <DimensionTable
          :rows="sorted"
          :header="HEADERS[config.dimension]"
          :unit="countUnit(countBy)"
          :show-count="config.showValues"
          :show-share="config.showPercent"
          :selectable="drillable"
          @select="select"
        />
      </div>
    </AsyncBoundary>

    <div v-if="isVendorLike" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      <p v-if="countBy === 'bindings'">
        <strong>Bindings</strong> are number + SIM + handset combinations. A dual-SIM phone serving two numbers counts
        twice, so this runs higher than the handset count for the same vendor. Switch to Handsets to compare.
      </p>
      <p v-else-if="countBy === 'subscribers'">
        <strong>Subscribers</strong> are distinct phone numbers, and these values <strong>overlap</strong>: someone owning
        a Samsung and an Apple handset is counted under both. The percentages therefore sum to more than 100%.
      </p>
      <p v-else>
        <strong>Handsets</strong> are distinct IMEIs. These do not overlap &mdash; a handset belongs to exactly one vendor
        &mdash; so the values add up. Excludes the <code class="font-mono">000000</code> population, which has no handset
        to count.
      </p>
    </div>
  </div>
</template>
