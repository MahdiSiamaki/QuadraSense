<script setup lang="ts">
import { computed } from 'vue'
import { useModelArrivals, useModelArrivalStatus } from '@/api/modelArrivals'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import ModelArrivalsChart from '@/features/devices/ModelArrivalsChart.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { ModelArrivalsConfig } from './config'
import { useKept } from '../overview/shared/useKept'

/**
 * How many models the feed named for the first time, per month or week, since the first daily file.
 * The page's own chart, unchanged; the caption keeps its warning about the first weeks.
 */
const props = defineProps<WidgetViewProps<ModelArrivalsConfig>>()
type Snapshot = SnapshotData<'device.modelArrivals'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)

const statusQuery = useModelArrivalStatus({ enabled: active })
const status = useWidgetData(props, statusQuery, (raw) => (raw as Snapshot).status)
const ready = computed(() => status.data.value?.ready === true)

const arrivalsQuery = useModelArrivals(() => config.value.grain, () => active.value && ready.value)
const arrivals = useWidgetData(props, arrivalsQuery, (raw) => (raw as Snapshot).arrivals ?? undefined)
const { value: data, loading: dataLoading, updating } = useKept(arrivals)

const loading = computed(() => status.isPending.value || (ready.value && dataLoading.value))
const dataStart = computed(() => formatDate(status.data.value?.dataStart ?? null))

function periodLabel(iso: string): string {
  return data.value?.grain === 'week' ? `Week of ${formatDate(iso)}` : iso.slice(0, 7)
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Models first seen per {{ config.grain }}, since the first daily file ({{ dataStart }}).
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="status.isError.value || arrivals.isError.value"
      :error="status.error.value ?? arrivals.error.value"
      :is-empty="ready && !loading && (data?.periods.length ?? 0) === 0"
      min-height="8rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : arrivals.refetch()"
    >
      <p
        v-if="status.data.value && !status.data.value.ready"
        class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 text-xs text-[var(--c-text-secondary)]"
        role="status"
      >
        {{ status.data.value.notReady }}
      </p>
      <template v-else-if="data">
        <div v-if="config.display === 'chart'" class="relative min-h-0 flex-1">
          <ModelArrivalsChart :data="data" height="100%" />
        </div>
        <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
          <table class="w-full text-xs">
            <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-2 py-1.5 font-medium">{{ data.grain === 'week' ? 'Week' : 'Month' }}</th>
                <th scope="col" class="px-2 py-1.5 text-right font-medium">Models first seen</th>
                <th scope="col" class="px-2 py-1.5 text-right font-medium">Known to GSMA</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="p in [...data.periods].reverse()" :key="p.periodStart" class="border-t">
                <td class="tabular px-2 py-1.5 whitespace-nowrap">{{ periodLabel(p.periodStart) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(p.models) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(p.knownModels) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      The first weeks after {{ dataStart }} are mostly models the initial dump's month happened to miss, not new arrivals.
    </p>
  </div>
</template>
