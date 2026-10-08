<script setup lang="ts">
import { computed } from 'vue'

/**
 * Progress through one stage of an import.
 *
 * Two modes, because a job genuinely has two situations. When the total is known the bar
 * fills; when it is not — a stage whose work cannot be counted ahead of time — it becomes an
 * indeterminate sweep rather than a bar frozen at some invented percentage.
 *
 * The percentage is never rounded up past 100 and never shown with more than one decimal: a
 * progress bar reading "99.94%" invites the reader to watch digits instead of work.
 */
const props = withDefaults(
  defineProps<{
    /** 0–100, or null when the total is unknown. */
    percent: number | null
    label?: string
    detail?: string
    tone?: 'accent' | 'success' | 'warning'
  }>(),
  { tone: 'accent' },
)

const width = computed(() => Math.max(0, Math.min(100, props.percent ?? 0)))

const colour = computed(
  () =>
    ({
      accent: 'var(--c-accent)',
      success: 'var(--c-success)',
      warning: 'var(--c-warning)',
    })[props.tone],
)
</script>

<template>
  <div class="space-y-1">
    <div
      v-if="label || detail || percent !== null"
      class="flex items-baseline justify-between gap-3 text-xs"
    >
      <span class="truncate font-medium text-[var(--c-text-secondary)]">{{ label }}</span>
      <span class="tabular shrink-0 text-[var(--c-text-muted)]">
        <template v-if="detail">{{ detail }}</template>
        <template v-if="detail && percent !== null"> · </template>
        <template v-if="percent !== null">{{ width.toFixed(1) }}%</template>
      </span>
    </div>

    <div
      class="relative h-1.5 w-full overflow-hidden rounded-full bg-[var(--c-surface-sunken)]"
      role="progressbar"
      :aria-valuenow="percent ?? undefined"
      aria-valuemin="0"
      aria-valuemax="100"
      :aria-label="label ?? 'Progress'"
    >
      <span
        v-if="percent !== null"
        class="absolute inset-y-0 left-0 rounded-full transition-[width] duration-500 ease-out"
        :style="{ width: `${width}%`, backgroundColor: colour }"
      />
      <!--
        Unknown total: a sweep that says "working" without claiming a position. With reduced motion
        it is a faint full-width bar instead: the global rule would otherwise run the sweep once in
        0.01ms and leave a still third at the left of the track, which reads as 33% done.
      -->
      <span
        v-else
        class="absolute inset-y-0 left-0 w-1/3 animate-indeterminate rounded-full motion-reduce:w-full motion-reduce:animate-none motion-reduce:opacity-40"
        :style="{ backgroundColor: colour }"
      />
    </div>
  </div>
</template>

