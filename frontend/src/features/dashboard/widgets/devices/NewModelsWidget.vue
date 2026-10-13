<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import { networkAgeText, useModelArrivalStatus, useNewModels } from '@/api/modelArrivals'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { NewModelsConfig } from './config'
import { useKept } from '../overview/shared/useKept'

/**
 * Device models the daily files named for the first time, in the dashboard's date range.
 *
 * First seen is the first daily file that named one of the model's handsets - not a launch date.
 * Models the initial dump listed were seen before the data starts and never appear. The range counts
 * back from the arrivals' own last day, and "all" is everything since the first daily file.
 */
const props = defineProps<WidgetViewProps<NewModelsConfig>>()
type Snapshot = SnapshotData<'device.newModels'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)

const statusQuery = useModelArrivalStatus({ enabled: active })
const status = useWidgetData(props, statusQuery, (raw) => (raw as Snapshot).status)
const ready = computed(() => status.data.value?.ready === true)

const window = computed(() => {
  const s = status.data.value
  if (!s?.dataThrough) return null
  const r = resolveRange(props.lens.range, s.dataThrough)
  return { from: r.from ?? s.dataStart, to: r.to ?? s.dataThrough }
})

const query = computed(() => ({
  from: window.value?.from,
  to: window.value?.to,
  brand: config.value.brand.trim() || undefined,
  deviceType: config.value.deviceType.trim() || undefined,
  knownOnly: config.value.knownOnly,
  page: 1,
  pageSize: config.value.limit,
}))
const listQuery = useNewModels(query, () => active.value && ready.value && window.value !== null)
const list = useWidgetData(props, listQuery, (raw) => (raw as Snapshot).models ?? undefined)
const { value: result, loading: listLoading, updating } = useKept(list)

const loading = computed(() => status.isPending.value || (ready.value && listLoading.value))
const caption = computed(() => {
  const parts: string[] = []
  if (config.value.brand.trim()) parts.push(`Brand contains “${config.value.brand.trim()}”.`)
  if (config.value.deviceType.trim()) parts.push(`${config.value.deviceType.trim()} only.`)
  parts.push(config.value.knownOnly ? 'Models known to GSMA only.' : 'Includes TACs not in GSMA.')
  return parts.join(' ')
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      First seen in the daily files - not a launch date. {{ caption }}
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="status.isError.value || list.isError.value"
      :error="status.error.value ?? list.error.value"
      min-height="8rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : list.refetch()"
    >
      <p
        v-if="status.data.value && !status.data.value.ready"
        class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 text-xs text-[var(--c-text-secondary)]"
        role="status"
      >
        {{ status.data.value.notReady }}
      </p>
      <template v-else-if="result">
        <div class="min-h-0 flex-1 overflow-auto overscroll-contain">
          <table class="w-full text-xs">
            <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-2 py-1.5 font-medium">Model</th>
                <th scope="col" class="px-2 py-1.5 font-medium">First seen</th>
                <th scope="col" class="px-2 py-1.5 text-right font-medium">Network age</th>
                <th scope="col" class="px-2 py-1.5 text-right font-medium">Handsets now, est.</th>
              </tr>
            </thead>
            <tbody>
              <tr v-if="result.rows.length === 0">
                <td colspan="4" class="px-2 py-6 text-center text-[var(--c-text-muted)]">No model was first seen in this period.</td>
              </tr>
              <tr v-for="m in result.rows" :key="m.tac" class="border-t">
                <td class="min-w-0 px-2 py-1.5">
                  <RouterLink
                    v-if="mode === 'view'"
                    :to="`/devices/${m.tac}`"
                    class="font-medium [overflow-wrap:anywhere] text-[var(--c-accent)] hover:underline"
                  >
                    {{ m.model ?? 'Model not in GSMA' }}
                  </RouterLink>
                  <span v-else class="font-medium [overflow-wrap:anywhere]">{{ m.model ?? 'Model not in GSMA' }}</span>
                  <span class="block text-2xs text-[var(--c-text-muted)]">
                    <template v-if="m.brand">{{ m.brand }} · </template><span class="tabular font-mono">{{ m.tac }}</span>
                  </span>
                </td>
                <td class="tabular px-2 py-1.5 whitespace-nowrap">{{ formatDate(m.firstSeen) }}</td>
                <td class="tabular px-2 py-1.5 text-right whitespace-nowrap">{{ networkAgeText(m.networkAgeDays, false) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ m.handsets === null ? '—' : formatFull(m.handsets) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
          <template v-if="result.total > result.rows.length">Top {{ result.rows.length }} of </template>
          {{ formatFull(result.total) }} {{ result.total === 1 ? 'model' : 'models' }} first seen between
          {{ formatDate(result.from) }} and {{ formatDate(result.to) }}.
          <RouterLink v-if="mode === 'view'" to="/devices/new-models" class="text-[var(--c-accent)] hover:underline">
            All new models →
          </RouterLink>
        </p>
      </template>
    </AsyncBoundary>
  </div>
</template>
