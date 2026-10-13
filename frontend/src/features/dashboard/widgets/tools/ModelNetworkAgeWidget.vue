<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import { useDevice } from '@/api/devices'
import { networkAgeText, useModelNetworkAge } from '@/api/modelArrivals'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { ModelNetworkAgeConfig } from './config'

/**
 * How long one model has been on this network: days since the feed first named one of its handsets.
 * Not the age of any handset, and a lower bound for a model the initial dump already listed.
 */
const props = defineProps<WidgetViewProps<ModelNetworkAgeConfig>>()
type Snapshot = SnapshotData<'tools.modelNetworkAge'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const ageQuery = useModelNetworkAge(() => config.value.tac, { enabled: active })
const deviceQuery = useDevice(() => config.value.tac, { enabled: () => active.value && config.value.showDetail })
const age = useWidgetData(props, ageQuery, (raw) => (raw as Snapshot).age ?? undefined)
const device = useWidgetData(props, deviceQuery, (raw) => (raw as Snapshot).device ?? undefined)

const a = computed(() => age.data.value)
const d = computed(() => device.data.value)
const name = computed(() => {
  const x = d.value
  if (!x) return null
  return [x.brand ?? x.manufacturer, x.marketingName ?? x.model].filter(Boolean).join(' ') || null
})
</script>

<template>
  <AsyncBoundary
    class="h-full"
    :is-loading="age.isPending.value"
    :is-error="age.isError.value"
    :error="age.error.value"
    min-height="3rem"
    gap="none"
    @retry="age.refetch()"
  >
    <div v-if="a" class="flex h-full flex-col gap-1 overflow-auto text-xs">
      <p v-if="config.showDetail" class="truncate text-[var(--c-text-secondary)]" :title="name ?? undefined">
        {{ name ?? (device.isPending.value ? '…' : 'Model not in GSMA') }}
        <span class="tabular font-mono text-[var(--c-text-muted)]"> · {{ a.tac }}</span>
      </p>
      <p class="tabular text-lg font-semibold tracking-tight">{{ networkAgeText(a.networkAgeDays, a.atLeast) }}</p>
      <p class="text-[var(--c-text-secondary)]">
        <template v-if="a.firstSeen">First seen {{ formatDate(a.firstSeen) }}<template v-if="a.inDump"> (in the initial dump)</template>, named on {{ formatFull(a.daysSeen) }} {{ a.daysSeen === 1 ? 'day' : 'days' }}.</template>
        <template v-else>Never seen in this data.</template>
      </p>
      <p class="text-2xs text-[var(--c-text-muted)]">
        Days since the feed first named one of its handsets - not the age of any handset.
        <RouterLink v-if="mode === 'view'" :to="`/devices/${a.tac}`" class="text-[var(--c-accent)] hover:underline">Open the device →</RouterLink>
      </p>
    </div>
  </AsyncBoundary>
</template>
