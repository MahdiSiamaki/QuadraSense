<script setup lang="ts">
import { computed } from 'vue'
import { formatCompact, formatFull } from '@/lib/format'

/**
 * A headline figure.
 *
 * Two decisions worth stating. First, the value is shown compactly (126M) but
 * carries the exact number in a title attribute - on a dashboard you want the
 * magnitude at a glance, and the precise figure when you go looking for it.
 * Second, there is no sparkline or decorative icon: at these sizes they add
 * pixels without adding information.
 */
const props = withDefaults(
  defineProps<{
    label: string
    value: number | null | undefined
    /** Shown under the value, e.g. a share or comparison. */
    hint?: string
    /** Renders the value as a percentage instead of a count. */
    percent?: boolean
    tone?: 'default' | 'muted' | 'warning'
  }>(),
  { tone: 'default', percent: false },
)

const display = computed(() => {
  if (props.value === null || props.value === undefined) return '—'
  if (props.percent) return `${props.value.toFixed(1)}%`
  return formatCompact(props.value)
})

const exact = computed(() =>
  props.value === null || props.value === undefined || props.percent
    ? undefined
    : formatFull(props.value),
)

const toneClass = computed(() =>
  ({
    default: 'text-[var(--c-text)]',
    muted: 'text-[var(--c-text-secondary)]',
    warning: 'text-[var(--c-warning)]',
  })[props.tone],
)
</script>

<template>
  <div class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] px-4 py-3.5 shadow-[var(--shadow-xs)]">
    <p class="text-[var(--text-xs)] font-medium tracking-wide text-[var(--c-text-muted)]">
      {{ label }}
    </p>
    <p
      class="kpi-value mt-1.5 text-[var(--text-2xl)] leading-none font-semibold"
      :class="toneClass"
      :title="exact"
    >
      {{ display }}
    </p>
    <p v-if="hint" class="mt-1.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
      {{ hint }}
    </p>
  </div>
</template>
