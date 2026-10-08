<script setup lang="ts">
import { computed } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { api } from '@/api/client'
import { formatBytes, formatFull } from '@/lib/format'

/**
 * The first rows of the file, exactly as delivered.
 *
 * Deliberately unformatted. The point of a preview is to see what the source actually sent — a
 * stray quote, a shifted column, a header that changed — and any tidying on the way to the
 * screen would hide precisely the thing being looked for. Monospaced, unaligned, raw.
 *
 * Loaded only when opened. These files reach a gigabyte, and reading the head of one on every
 * detail-page view would be a cost nobody asked for.
 */
const props = defineProps<{ jobId: number; open: boolean }>()

interface Preview {
  fileName: string
  fileBytes: number
  columns: string[]
  rows: string[]
  totalRows: number
}

const preview = useQuery({
  queryKey: ['imports', 'preview', computed(() => props.jobId)],
  queryFn: ({ signal }) =>
    api.get<Preview>(`/api/v1/imports/${props.jobId}/preview`, { lines: 20 }, signal),
  enabled: computed(() => props.open),
  staleTime: Number.POSITIVE_INFINITY, // the file is immutable once stored
})
</script>

<template>
  <div v-if="open" class="space-y-2">
    <p v-if="preview.isPending.value" class="text-xs text-[var(--c-text-muted)]">
      Reading the first rows…
    </p>

    <p
      v-else-if="preview.isError.value"
      class="text-xs text-[var(--c-text-secondary)]"
    >
      The original file could not be read. It may have been deleted.
    </p>

    <template v-else-if="preview.data.value">
      <p class="text-2xs text-[var(--c-text-muted)]">
        {{ preview.data.value.columns.length }} {{ preview.data.value.columns.length === 1 ? 'column' : 'columns' }} ·
        {{ formatBytes(preview.data.value.fileBytes) }}
        <template v-if="preview.data.value.totalRows > 0">
          · {{ formatFull(preview.data.value.totalRows) }} {{ preview.data.value.totalRows === 1 ? 'row' : 'rows' }} in total
        </template>
        · showing the first {{ preview.data.value.rows.length }}
      </p>

      <div class="overflow-x-auto rounded-[var(--radius-md)] border">
        <table class="w-full border-collapse font-mono text-2xs">
          <thead class="bg-[var(--c-surface-sunken)]">
            <tr>
              <th scope="col" class="px-2 py-1 text-right font-medium text-[var(--c-text-muted)]">
                #
              </th>
              <th
                v-for="column in preview.data.value.columns"
                :key="column"
                scope="col"
                class="px-2 py-1 text-left font-medium whitespace-nowrap"
              >
                {{ column }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, index) in preview.data.value.rows" :key="index" class="border-t">
              <td class="tabular px-2 py-0.5 text-right text-[var(--c-text-muted)]">
                {{ index + 2 }}
              </td>
              <!--
                Split for display only, and never re-joined or re-quoted. A row with more fields
                than the header has is exactly the kind of defect worth seeing, so the extra
                cells are shown rather than dropped.
              -->
              <td
                v-for="(cell, cellIndex) in row.split(',')"
                :key="cellIndex"
                class="px-2 py-0.5 whitespace-nowrap"
              >
                {{ cell }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </div>
</template>
