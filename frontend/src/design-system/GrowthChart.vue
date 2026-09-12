<script setup lang="ts">
import { computed } from 'vue'
import { formatCompact, formatSigned } from '@/lib/format'
import type { GrowthRow } from '@/api/dashboard'

/**
 * Net gain and loss per vendor, as a diverging bar chart around zero.
 *
 * A diverging layout is the right form here because the value has a meaningful sign and a
 * meaningful zero: growing and shrinking are opposites, not just different magnitudes. Sorted
 * by net so the two groups separate naturally and the reader can see where the line falls.
 *
 * Built from divs rather than ECharts: it is a sorted list of labelled bars around a centre
 * line, and the layout is easier to control in CSS than to configure in a chart library.
 */
const props = defineProps<{ data: GrowthRow[] }>()

const rows = computed(() => {
  // A single scale across both directions, so a bar twice as long really is twice the change.
  const widest = Math.max(...props.data.map((d) => Math.abs(d.net)), 1)
  return props.data.map((d) => ({
    ...d,
    width: (Math.abs(d.net) / widest) * 50, // percent of full width, half each side
    positive: d.net >= 0,
  }))
})
</script>

<template>
  <div class="space-y-1">
    <div v-for="row in rows" :key="row.key" class="group flex items-center gap-2 py-0.5">
      <span class="w-24 shrink-0 truncate text-right text-[var(--text-xs)]" :title="row.key">
        {{ row.key }}
      </span>

      <!-- The centre line is the zero point; bars grow left for loss, right for gain. -->
      <div class="relative h-5 flex-1">
        <span
          aria-hidden="true"
          class="absolute inset-y-0 left-1/2 w-px bg-[var(--c-border-strong)]"
        />
        <span
          class="absolute inset-y-0.5 rounded-[2px]"
          :style="{
            width: `${row.width}%`,
            left: row.positive ? '50%' : `${50 - row.width}%`,
            backgroundColor: row.positive ? 'var(--viz-3)' : 'var(--viz-6)',
          }"
        />
      </div>

      <span
        class="tabular w-20 shrink-0 text-right text-[var(--text-xs)] font-medium"
        :class="row.positive ? 'text-[var(--viz-3)]' : 'text-[var(--viz-6)]'"
        :title="`added ${formatCompact(row.added)}, removed ${formatCompact(row.removed)}`"
      >
        {{ formatSigned(row.net) }}
      </span>
    </div>

    <p class="border-t pt-2 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
      Net bindings gained minus lost. Hover a figure for the gross adds and removes behind it.
    </p>
  </div>
</template>
