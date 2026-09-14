<script setup lang="ts">
import { computed } from 'vue'
import { statusLabel, statusTone, type ImportStatus } from '@/api/imports'

/**
 * The status of one import, as a badge.
 *
 * Colour is never the only signal. A running job also carries a pulsing dot, a failed one a
 * heavier weight, and every badge states its status in words — so the badge survives being
 * read by someone who cannot distinguish the hues, printed in grey, or glanced at quickly.
 */
const props = defineProps<{ status: ImportStatus; compact?: boolean }>()

const tone = computed(() => statusTone(props.status))
const label = computed(() => statusLabel(props.status))

const styles: Record<string, { bg: string; fg: string; dot: string }> = {
  success: { bg: 'var(--c-success-subtle)', fg: 'var(--c-success)', dot: 'var(--c-success)' },
  warning: { bg: 'var(--c-warning-subtle)', fg: 'var(--c-warning)', dot: 'var(--c-warning)' },
  danger: { bg: 'var(--c-danger-subtle)', fg: 'var(--c-danger)', dot: 'var(--c-danger)' },
  running: { bg: 'var(--c-accent-subtle)', fg: 'var(--c-accent)', dot: 'var(--c-accent)' },
  neutral: {
    bg: 'var(--c-surface-sunken)',
    fg: 'var(--c-text-secondary)',
    dot: 'var(--c-text-muted)',
  },
}

const style = computed(() => styles[tone.value] ?? styles['neutral']!)
</script>

<template>
  <span
    class="inline-flex items-center gap-1.5 rounded-full font-medium whitespace-nowrap"
    :class="compact ? 'px-1.5 py-0.5 text-[var(--text-2xs)]' : 'px-2 py-0.5 text-[var(--text-xs)]'"
    :style="{ backgroundColor: style.bg, color: style.fg }"
  >
    <span
      class="size-1.5 shrink-0 rounded-full"
      :class="tone === 'running' ? 'animate-pulse' : ''"
      :style="{ backgroundColor: style.dot }"
      aria-hidden="true"
    />
    {{ label }}
  </span>
</template>
