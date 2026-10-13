<script setup lang="ts">
import { computed } from 'vue'
import { useDeviceClassMix } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import CompositionBar from '@/design-system/CompositionBar.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { ClassMixConfig } from './config'
import { countUnit } from '../overview/shared/countBy'
import { lensCountBy } from '../overview/shared/filters'
import { useKept } from '../overview/shared/useKept'

/**
 * Handsets against machines: smartphone, feature phone, tablet, IoT/M2M. From a pre-computed table
 * with no dimension breakdown, so it is network-wide - the card says so when the dashboard filters.
 */
const props = defineProps<WidgetViewProps<ClassMixConfig>>()

const active = useQueryActive(props)
const countBy = computed(() => lensCountBy(props.lens))
const query = useDeviceClassMix(countBy, { enabled: active })
const data = useWidgetData(props, query, (raw) => (raw as SnapshotData<'device.classMix'>).rows)
const { value: rows, loading, updating } = useKept(data)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Counted by {{ countBy }}<template v-if="countBy !== 'bindings'"> (estimated)</template>. IoT/M2M is a real segment
      here, not tail noise.
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="data.isError.value"
      :error="data.error.value"
      :is-empty="!loading && (rows?.length ?? 0) === 0"
      min-height="4rem"
      gap="none"
      @retry="data.refetch()"
    >
      <CompositionBar v-if="instance.config.display === 'composition'" :data="rows ?? []" />
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <DimensionTable :rows="rows ?? []" header="Device class" :unit="countUnit(countBy)" :selectable="false" />
      </div>
    </AsyncBoundary>
  </div>
</template>
