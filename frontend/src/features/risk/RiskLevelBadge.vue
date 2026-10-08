<script setup lang="ts">
import { computed } from 'vue'
import { LEVEL_LABELS, type RiskLevel } from '@/api/risk'

/**
 * A risk level, always in words as well as colour.
 *
 * Observation is neutral, Anomaly the accent (out of line, not evidence about anyone), Risk signal
 * the warning tone, and a suspected pattern the strongest. Danger red is not used: nothing the
 * system shows is a finding of fraud, and the colour would say otherwise.
 */
const props = defineProps<{ level: RiskLevel; capped?: boolean; compact?: boolean }>()

// fg for the words, dot for the dot: a mark keeps the plain status colour.
const TONES: Record<RiskLevel, { fg: string; dot: string; bg: string; weight: string }> = {
  Observation: { fg: 'var(--c-text-secondary)', dot: 'var(--c-text-secondary)', bg: 'var(--c-surface-sunken)', weight: 'font-medium' },
  Anomaly: { fg: 'var(--c-accent)', dot: 'var(--c-accent)', bg: 'var(--c-accent-subtle)', weight: 'font-medium' },
  RiskSignal: { fg: 'var(--c-warning-text)', dot: 'var(--c-warning)', bg: 'var(--c-warning-subtle)', weight: 'font-semibold' },
  SuspiciousPattern: { fg: 'var(--c-warning-text)', dot: 'var(--c-warning)', bg: 'var(--c-warning-subtle)', weight: 'font-bold' },
  ConfirmedFraud: { fg: 'var(--c-text)', dot: 'var(--c-text)', bg: 'var(--c-surface-sunken)', weight: 'font-semibold' },
}

const tone = computed(() => TONES[props.level])
const label = computed(() => LEVEL_LABELS[props.level] + (props.capped ? ' (held by feed defects)' : ''))
</script>

<template>
  <span
    class="inline-flex items-center gap-1.5 rounded-full whitespace-nowrap"
    :class="[tone.weight, compact ? 'px-1.5 py-0.5 text-2xs' : 'px-2 py-0.5 text-xs']"
    :style="{ color: tone.fg, backgroundColor: tone.bg }"
  >
    <span class="size-1.5 shrink-0 rounded-full" :style="{ backgroundColor: tone.dot }" aria-hidden="true" />
    {{ label }}
  </span>
</template>
