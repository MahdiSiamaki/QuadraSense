<script setup lang="ts">
import { computed } from 'vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useFreshness, useWorkerHealth } from '@/features/imports/useImportQueries'
import { formatDateTime, formatFull, formatRelative } from '@/lib/format'
import type { WorkerHealthConfig } from './config'

/**
 * The import workers: how many are alive, what they hold, whether a lease has gone stale, and -
 * optionally - whether the dashboard's figures are waiting for a run of queued files to land.
 */
const props = defineProps<WidgetViewProps<WorkerHealthConfig>>()
type Snapshot = SnapshotData<'ops.workerHealth'>

const active = useQueryActive(props)
const healthQuery = useWorkerHealth({ enabled: active })
const freshnessQuery = useFreshness({ enabled: () => active.value && props.instance.config.showPendingRebuild })
const health = useWidgetData(props, healthQuery, (raw) => (raw as Snapshot).workerHealth)
const freshness = useWidgetData(props, freshnessQuery, (raw) => (raw as Snapshot).freshness)

const h = computed(() => health.data.value)
const pendingSince = computed(() => freshness.data.value?.find((f) => f.sourceCode === 'SQM')?.dashboardPendingSince ?? null)
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
    <div v-if="h" class="flex h-full flex-col gap-1.5 overflow-auto text-xs">
      <p class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <span
          class="size-2 shrink-0 rounded-full"
          :class="h.stalled ? 'bg-[var(--c-danger)]' : h.activeWorkers > 0 ? 'bg-[var(--c-success)]' : 'bg-[var(--c-text-muted)]'"
          aria-hidden="true"
        />
        {{ formatFull(h.activeWorkers) }} {{ h.activeWorkers === 1 ? 'worker' : 'workers' }} alive
      </p>
      <p v-if="h.stalled" class="text-[var(--c-danger-text)]" role="status">Stalled: work is waiting and nothing is alive to take it.</p>
      <p class="tabular text-[var(--c-text-secondary)]">
        {{ formatFull(h.running) }} running · {{ formatFull(h.queued) }} queued<template v-if="h.oldestQueuedAt">
          (oldest <span :title="formatDateTime(h.oldestQueuedAt)">{{ formatRelative(h.oldestQueuedAt) }}</span>)</template>
      </p>
      <p v-if="h.staleLeases > 0" class="text-[var(--c-warning-text)]">
        {{ formatFull(h.staleLeases) }} stale {{ h.staleLeases === 1 ? 'lease' : 'leases' }}: a job a worker stopped renewing.
      </p>
      <p v-if="h.workerHostedInApi" class="text-2xs text-[var(--c-text-muted)]">A worker runs inside the API process: restarting the API stops its import.</p>
      <p v-if="instance.config.showPendingRebuild && pendingSince" class="text-[var(--c-warning-text)]">
        Dashboard figures wait for the queued files (since {{ formatDateTime(pendingSince) }}).
      </p>
    </div>
  </AsyncBoundary>
</template>
