<script setup lang="ts">
import { computed } from 'vue'
import { formatCompact, formatFull } from '@/lib/format'

/**
 * A headline figure that says what it counts.
 *
 * The label alone is ambiguous in this domain: "Subscribers" could reasonably mean
 * phone numbers or SIM cards, and "Devices" could mean handsets or IMEIs including the
 * unknown-device sentinel. Every card therefore carries the identifier it counts, in
 * monospace, directly under the number — so the reading is unambiguous at a glance
 * rather than something you have to remember or look up.
 *
 * The full definition, including any caveat, is on the card's tooltip.
 *
 * The value is shown compactly (125.9M) with the exact figure in the title attribute:
 * on a dashboard you want the magnitude at a glance and the precise number when you go
 * looking for it.
 */
const props = withDefaults(
  defineProps<{
    label: string
    value: number | null | undefined
    /** The identifier this counts, e.g. "MSISDN". Rendered in monospace. */
    identifier?: string
    /** Extra qualifier shown next to the identifier, e.g. "approx." */
    qualifier?: string
    /** Full definition, shown on hover. Say what is included and what is not. */
    definition?: string
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

const tooltip = computed(() =>
  [props.definition, exact.value ? `Exact: ${exact.value}` : null].filter(Boolean).join('\n'),
)

const toneClass = computed(
  () =>
    ({
      default: 'text-[var(--c-text)]',
      muted: 'text-[var(--c-text-secondary)]',
      warning: 'text-[var(--c-warning)]',
    })[props.tone],
)
</script>

<template>
  <div
    class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] px-4 py-3.5 shadow-[var(--shadow-xs)]"
    :title="tooltip || undefined"
  >
    <p class="text-xs font-medium tracking-wide text-[var(--c-text-muted)]">
      {{ label }}
    </p>

    <p class="kpi-value mt-1.5 text-2xl leading-none font-semibold" :class="toneClass">
      {{ display }}
    </p>

    <p v-if="identifier || qualifier" class="mt-2 flex flex-wrap items-baseline gap-x-1.5">
      <code
        v-if="identifier"
        class="rounded-[var(--radius-sm)] bg-[var(--c-surface-sunken)] px-1.5 py-0.5 font-mono text-2xs text-[var(--c-text-secondary)]"
      >
        {{ identifier }}
      </code>
      <span v-if="qualifier" class="text-2xs text-[var(--c-text-muted)]">
        {{ qualifier }}
      </span>
    </p>
  </div>
</template>
