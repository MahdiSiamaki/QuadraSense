<script setup lang="ts">
import { RouterLink } from 'vue-router'
import StatusBadge from '@/design-system/StatusBadge.vue'
import { formatBytes, formatDate, formatDuration, formatFull, formatRelative, formatDateTime } from '@/lib/format'
import type { ImportJobSummary } from '@/api/imports'

/**
 * Import history.
 *
 * Column order follows how the row is actually read: what day is this, what happened to it,
 * how much came in, when. The file name is the widest column and comes first because it is
 * what an operator recognises, but the business date is what they are usually looking for —
 * so it sits immediately beside it rather than at the far end of the row.
 *
 * A superseded row is dimmed rather than hidden. A day that was imported twice is a fact worth
 * being able to see; hiding the earlier attempt would make the history disagree with itself.
 */
defineProps<{ rows: ImportJobSummary[] }>()
</script>

<template>
  <div class="overflow-x-auto">
    <table class="w-full border-collapse text-[var(--text-sm)]">
      <thead>
        <tr class="border-b text-left text-[var(--text-xs)] text-[var(--c-text-muted)]">
          <th scope="col" class="px-3 py-2 font-medium">File</th>
          <th scope="col" class="px-3 py-2 font-medium">Day</th>
          <th scope="col" class="px-3 py-2 font-medium">Status</th>
          <th scope="col" class="px-3 py-2 text-right font-medium">Rows in</th>
          <th scope="col" class="px-3 py-2 text-right font-medium">Imported</th>
          <th scope="col" class="px-3 py-2 text-right font-medium">Quarantined</th>
          <th scope="col" class="px-3 py-2 text-right font-medium">Took</th>
          <th scope="col" class="px-3 py-2 text-right font-medium">Finished</th>
        </tr>
      </thead>

      <tbody>
        <tr
          v-for="row in rows"
          :key="row.jobId"
          class="border-b transition-colors last:border-0 hover:bg-[var(--c-surface-hover)]"
          :class="row.isEffective ? '' : 'text-[var(--c-text-muted)]'"
        >
          <td class="max-w-[22rem] px-3 py-2">
            <RouterLink
              :to="`/imports/${row.jobId}`"
              class="block truncate font-medium text-[var(--c-text)] hover:text-[var(--c-accent)] hover:underline"
              :title="row.originalFileName"
            >
              {{ row.originalFileName }}
            </RouterLink>
            <span class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
              {{ row.sourceCode }} · {{ formatBytes(row.fileBytes) }}
              <template v-if="row.revision > 1"> · revision {{ row.revision }}</template>
              <template v-if="!row.isEffective && row.status === 'Completed'"> · superseded</template>
            </span>
          </td>

          <td class="tabular px-3 py-2 whitespace-nowrap">{{ formatDate(row.businessDate) }}</td>

          <td class="px-3 py-2">
            <StatusBadge :status="row.status" />
            <span
              v-if="row.attempt > 1"
              class="ml-1 text-[var(--text-2xs)] text-[var(--c-text-muted)]"
            >
              attempt {{ row.attempt }}
            </span>
          </td>

          <td class="tabular px-3 py-2 text-right">{{ formatFull(row.rowsInput) }}</td>
          <td class="tabular px-3 py-2 text-right">{{ formatFull(row.rowsInserted) }}</td>

          <td class="tabular px-3 py-2 text-right">
            <span :class="row.rowsInvalid > 0 ? 'text-[var(--c-warning)]' : ''">
              {{ row.rowsInvalid > 0 ? formatFull(row.rowsInvalid) : '—' }}
            </span>
          </td>

          <td class="tabular px-3 py-2 text-right whitespace-nowrap">
            {{ formatDuration(row.durationMs) }}
          </td>

          <td
            class="px-3 py-2 text-right whitespace-nowrap"
            :title="formatDateTime(row.finishedAt ?? row.createdAt)"
          >
            {{ formatRelative(row.finishedAt ?? row.createdAt) }}
          </td>
        </tr>
      </tbody>
    </table>

    <p v-if="!rows.length" class="px-3 py-8 text-center text-[var(--text-sm)] text-[var(--c-text-muted)]">
      No imports match these filters.
    </p>
  </div>
</template>
