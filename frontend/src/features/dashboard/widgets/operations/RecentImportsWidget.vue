<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import StatusBadge from '@/design-system/StatusBadge.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useImportHistory } from '@/features/imports/useImportQueries'
import { formatDate, formatFull, formatRelative, formatDateTime } from '@/lib/format'
import type { RecentImportsConfig } from './config'
import { useKept } from '../overview/shared/useKept'

/**
 * The newest import jobs, optionally of one source or in chosen states - the Import Center's history,
 * shortened. It polls while a listed job is still running, as the Import Center does.
 */
const props = defineProps<WidgetViewProps<RecentImportsConfig>>()

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const query = useImportHistory(
  () => ({
    source: config.value.source === 'any' ? undefined : config.value.source,
    status: config.value.statuses.length ? config.value.statuses.join(',') : undefined,
    page: 1,
    pageSize: config.value.limit,
  }),
  { enabled: active },
)
const history = useWidgetData(props, query, (raw) => (raw as SnapshotData<'ops.recentImports'>).page)
const { value: page, loading, updating } = useKept(history)

const caption = computed(() => {
  const parts = [config.value.source === 'any' ? 'Every source' : `${config.value.source} files`]
  if (config.value.statuses.length) parts.push(`in ${config.value.statuses.length === 1 ? 'state' : 'states'} ${config.value.statuses.join(', ')}`)
  return `${parts.join(' ')}, newest first.`
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">{{ caption }}</p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="history.isError.value"
      :error="history.error.value"
      :is-empty="!loading && (page?.items.length ?? 0) === 0"
      empty-message="No import matches."
      min-height="6rem"
      gap="none"
      @retry="history.refetch()"
    >
      <div class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">File</th>
              <th scope="col" class="px-2 py-1.5 font-medium">Day</th>
              <th scope="col" class="px-2 py-1.5 font-medium">Status</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Rows in</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Finished</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="row in page?.items ?? []" :key="row.jobId" class="border-t" :class="row.isEffective ? '' : 'text-[var(--c-text-muted)]'">
              <td class="max-w-[16rem] px-2 py-1.5">
                <RouterLink
                  v-if="mode === 'view'"
                  :to="`/imports/${row.jobId}`"
                  class="block truncate font-medium hover:text-[var(--c-accent)] hover:underline"
                  :title="row.originalFileName"
                >
                  {{ row.originalFileName }}
                </RouterLink>
                <span v-else class="block truncate font-medium" :title="row.originalFileName">{{ row.originalFileName }}</span>
                <span class="text-2xs text-[var(--c-text-muted)]">{{ row.sourceCode }}<template v-if="!row.isEffective && row.status === 'Completed'"> · superseded</template></span>
              </td>
              <td class="tabular px-2 py-1.5 whitespace-nowrap">{{ formatDate(row.businessDate) }}</td>
              <td class="px-2 py-1.5 whitespace-nowrap"><StatusBadge :status="row.status" /></td>
              <td class="tabular px-2 py-1.5 text-right">{{ formatFull(row.rowsInput) }}</td>
              <td class="tabular px-2 py-1.5 text-right whitespace-nowrap" :title="row.finishedAt ? formatDateTime(row.finishedAt) : undefined">
                {{ row.finishedAt ? formatRelative(row.finishedAt) : '—' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>
    <p v-if="page" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      {{ formatFull(page.items.length) }} of {{ formatFull(page.total) }}.
      <RouterLink v-if="mode === 'view'" to="/imports" class="text-[var(--c-accent)] hover:underline">Open the Import Center →</RouterLink>
    </p>
  </div>
</template>
