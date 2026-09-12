<script setup lang="ts">
import { computed } from 'vue'
import { formatFull, formatPercent } from '@/lib/format'

/**
 * A ranked table with an inline proportion bar.
 *
 * The bar is drawn behind the row rather than in its own column. That keeps the
 * numbers in a single scannable column while still letting the eye compare
 * magnitudes — a table and a bar chart in the space of a table.
 *
 * Widths are relative to the largest row, not to the total, because the question
 * these tables answer is "how do these compare to each other", not "what share of
 * everything is this".
 */
const props = defineProps<{
  rows: Array<{ key: string; count: number; percent: number }>
  header: string
  /** Column heading for the measure, so the figures are never an unlabelled quantity. */
  unit?: string
}>()

defineEmits<{ select: [key: string] }>()

const max = computed(() => Math.max(...props.rows.map((r) => r.count), 1))

function isUnknown(key: string): boolean {
  return key.startsWith('(unknown')
}
</script>

<template>
  <table class="w-full text-[var(--text-sm)]">
    <thead>
      <tr class="border-b text-left text-[var(--text-xs)] text-[var(--c-text-muted)]">
        <th class="px-4 py-2 font-medium">{{ header }}</th>
        <th class="px-4 py-2 text-right font-medium capitalize">{{ unit ?? 'bindings' }}</th>
        <th class="px-4 py-2 text-right font-medium">Share</th>
      </tr>
    </thead>
    <tbody>
      <tr
        v-for="row in rows"
        :key="row.key"
        class="relative border-b last:border-0"
        :class="isUnknown(row.key) ? '' : 'cursor-pointer hover:bg-[var(--c-surface-hover)]'"
        @click="!isUnknown(row.key) && $emit('select', row.key)"
      >
        <td class="relative px-4 py-2.5">
          <!-- The proportion bar. aria-hidden because the figures beside it already
               carry the same information to a screen reader. -->
          <span
            aria-hidden="true"
            class="absolute inset-y-1 left-0 rounded-r-[2px] opacity-30"
            :style="{
              width: `${(row.count / max) * 100}%`,
              backgroundColor: isUnknown(row.key) ? 'var(--viz-null)' : 'var(--viz-1)',
            }"
          />
          <span
            class="relative"
            :class="isUnknown(row.key) ? 'text-[var(--c-text-muted)] italic' : ''"
          >
            {{ row.key }}
          </span>
        </td>
        <td class="relative px-4 py-2.5 text-right tabular">{{ formatFull(row.count) }}</td>
        <td class="relative px-4 py-2.5 text-right tabular text-[var(--c-text-secondary)]">
          {{ formatPercent(row.percent, 2) }}
        </td>
      </tr>
    </tbody>
  </table>
</template>
