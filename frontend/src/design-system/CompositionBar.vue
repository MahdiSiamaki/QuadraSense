<script setup lang="ts">
import { computed } from 'vue'
import { formatFull, formatPercent } from '@/lib/format'

/**
 * A single stacked bar showing how a whole divides into parts.
 *
 * Chosen over a pie or donut on purpose. Both encode proportion as angle, which
 * people compare poorly, and both fall apart past four or five slices. A single
 * stacked bar encodes proportion as length — the easiest visual comparison there
 * is — reads left to right, and stays legible with eight categories.
 *
 * Built from divs rather than a charting library: it is a row of rectangles, and
 * pulling ECharts in for that would add a dependency to no purpose.
 */
const props = defineProps<{
  data: Array<{ key: string; count: number; percent: number }>
}>()

/** Categories that mean "we do not know", drawn in grey rather than as a colour. */
const UNKNOWN_KEYS = new Set(['Unknown device', 'Unregistered TAC', 'Other'])

const segments = computed(() =>
  props.data.map((d, i) => ({
    ...d,
    color: UNKNOWN_KEYS.has(d.key) ? 'var(--viz-null)' : `var(--viz-${(i % 8) + 1})`,
    // Below roughly 2% a segment is too thin to label in place; the legend carries it.
    showInlineLabel: d.percent >= 6,
  })),
)
</script>

<template>
  <div class="space-y-3">
    <div
      class="flex h-9 w-full overflow-hidden rounded-[var(--radius-md)]"
      role="img"
      :aria-label="segments.map((s) => `${s.key} ${formatPercent(s.percent)}`).join(', ')"
    >
      <div
        v-for="s in segments"
        :key="s.key"
        class="group relative flex items-center justify-center transition-opacity hover:opacity-85"
        :style="{ width: `${s.percent}%`, backgroundColor: s.color }"
        :title="`${s.key}: ${formatFull(s.count)} (${formatPercent(s.percent, 2)})`"
      >
        <span
          v-if="s.showInlineLabel"
          class="px-1 text-[var(--text-2xs)] font-semibold text-white/95 drop-shadow-sm"
        >
          {{ formatPercent(s.percent, 0) }}
        </span>
      </div>
    </div>

    <ul class="flex flex-wrap gap-x-5 gap-y-1.5">
      <li v-for="s in segments" :key="s.key" class="flex items-center gap-1.5">
        <span class="size-2.5 shrink-0 rounded-[2px]" :style="{ backgroundColor: s.color }" />
        <span class="text-[var(--text-xs)] text-[var(--c-text-secondary)]">{{ s.key }}</span>
        <span class="tabular text-[var(--text-xs)] font-medium">{{ formatPercent(s.percent, 1) }}</span>
      </li>
    </ul>
  </div>
</template>
