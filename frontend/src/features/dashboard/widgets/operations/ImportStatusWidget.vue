<script setup lang="ts">
import { computed } from 'vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useFreshness, useWorkerHealth } from '@/features/imports/useImportQueries'
import { formatDateTime, formatFull, formatRelative } from '@/lib/format'
import type { ImportStatusConfig } from './config'

/**
 * The import pipeline in one line: running, queued or idle, what failed in the last day, and when a
 * daily file last landed. Says "stalled" in words when work is waiting and no worker is alive.
 */
const props = defineProps<WidgetViewProps<ImportStatusConfig>>()
type Snapshot = SnapshotData<'ops.importStatus'>

const active = useQueryActive(props)
const healthQuery = useWorkerHealth({ enabled: active })
const freshnessQuery = useFreshness({ enabled: active })
const health = useWidgetData(props, healthQuery, (raw) => (raw as Snapshot).workerHealth)
const freshness = useWidgetData(props, freshnessQuery, (raw) => (raw as Snapshot).freshness)

const h = computed(() => health.data.value)
const sqm = computed(() => freshness.data.value?.find((f) => f.sourceCode === 'SQM') ?? null)

const headline = computed(() => {
  const x = h.value
  if (!x) return { text: '…', tone: 'var(--c-text)' }
  if (x.stalled) return { text: 'Stalled', tone: 'var(--c-danger-text)' }
  if (x.running > 0) return { text: `${formatFull(x.running)} running`, tone: 'var(--c-text)' }
  if (x.queued > 0) return { text: `${formatFull(x.queued)} queued`, tone: 'var(--c-text)' }
  return { text: 'Idle', tone: 'var(--c-text)' }
})
</script>

<template>
  <AsyncBoundary
    class="h-full"
    :is-loading="health.isPending.value"
    :is-error="health.isError.value"
    :error="health.error.value"
    min-height="3rem"
    gap="none"
    @retry="health.refetch()"
  >
    <div v-if="h" class="flex h-full flex-col gap-1 overflow-auto">
      <p class="text-lg font-semibold tracking-tight" :style="{ color: headline.tone }">{{ headline.text }}</p>
      <p v-if="h.stalled" class="text-xs text-[var(--c-danger-text)]" role="status">Work is waiting and no worker is alive to take it.</p>
      <p class="tabular text-xs text-[var(--c-text-secondary)]">
        {{ formatFull(h.queued) }} queued · {{ formatFull(h.retrying) }} retrying ·
        <span :class="h.failedLast24Hours > 0 ? 'text-[var(--c-danger-text)]' : ''">{{ formatFull(h.failedLast24Hours) }} failed in 24 hours</span>
      </p>
      <p v-if="sqm?.latestImportedAt" class="text-2xs text-[var(--c-text-muted)]" :title="formatDateTime(sqm.latestImportedAt)">
        Last daily file landed {{ formatRelative(sqm.latestImportedAt) }}.
      </p>
    </div>
  </AsyncBoundary>
</template>
