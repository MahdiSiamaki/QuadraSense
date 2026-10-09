<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useBlockages } from '@/features/imports/useImportQueries'
import { formatDate, formatFull } from '@/lib/format'
import type { BlockagesConfig } from './config'

/**
 * Days that did not land and hold the later days of their source back: the job to import again, and
 * how many days wait behind it. Empty is the good state, and says so.
 */
const props = defineProps<WidgetViewProps<BlockagesConfig>>()

const active = useQueryActive(props)
const query = useBlockages({ enabled: active })
const blockages = useWidgetData(props, query, (raw) => (raw as SnapshotData<'ops.blockages'>).blockages)

const rows = computed(() => (blockages.data.value ?? []).slice(0, props.instance.config.limit))
const total = computed(() => blockages.data.value?.length ?? 0)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="blockages.isPending.value"
      :is-error="blockages.isError.value"
      :error="blockages.error.value"
      :is-empty="!blockages.isPending.value && total === 0"
      empty-message="Nothing is blocked: every failed day has been imported again or has nothing waiting behind it."
      min-height="4rem"
      gap="none"
      @retry="blockages.refetch()"
    >
      <ul class="min-h-0 flex-1 divide-y overflow-auto overscroll-contain text-xs">
        <li v-for="b in rows" :key="`${b.sourceCode}:${b.businessDate}`" class="flex items-baseline gap-3 py-1.5">
          <span class="tabular w-24 shrink-0 font-medium">{{ formatDate(b.businessDate) }}</span>
          <span class="min-w-0 flex-1 text-[var(--c-text-secondary)]">
            {{ b.sourceCode }} · {{ b.status === 'FAILED' ? 'failed' : 'cancelled' }} ·
            <span class="text-[var(--c-warning-text)]">{{ formatFull(b.waiting) }} later {{ b.waiting === 1 ? 'day waits' : 'days wait' }}</span>
          </span>
          <RouterLink v-if="mode === 'view'" :to="`/imports/${b.jobId}`" class="shrink-0 text-[var(--c-accent)] hover:underline">Open job</RouterLink>
        </li>
      </ul>
    </AsyncBoundary>
    <p v-if="total > rows.length" class="shrink-0 text-2xs text-[var(--c-text-muted)]">The first {{ rows.length }} of {{ formatFull(total) }}.</p>
  </div>
</template>
