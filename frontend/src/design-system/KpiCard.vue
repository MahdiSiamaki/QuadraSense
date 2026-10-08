<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from 'vue'
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
 *
 * A title is reachable only by a mouse, so the same two lines also sit behind a small "i"
 * button beside the label: a keyboard or a phone opens them there. They open over the card
 * rather than inside it - a card that grew would stretch every card in its row.
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

const infoOpen = ref(false)
const infoId = useId()
const root = ref<HTMLElement | null>(null)
const infoButton = ref<HTMLButtonElement | null>(null)

function onDocumentPointerDown(event: PointerEvent) {
  if (root.value && !root.value.contains(event.target as Node)) infoOpen.value = false
}

function onInfoKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  infoOpen.value = false
  infoButton.value?.focus()
}

/** Tabbing on past the card closes it, as clicking elsewhere does: no popovers left behind. */
function onFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (infoOpen.value && next && root.value && !root.value.contains(next)) infoOpen.value = false
}

// Listening only while open: the dashboard has six of these cards, data quality three.
watch(infoOpen, (open) => {
  if (open) document.addEventListener('pointerdown', onDocumentPointerDown)
  else document.removeEventListener('pointerdown', onDocumentPointerDown)
})
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))

const toneClass = computed(
  () =>
    ({
      default: 'text-[var(--c-text)]',
      muted: 'text-[var(--c-text-secondary)]',
      warning: 'text-[var(--c-warning-text)]',
    })[props.tone],
)
</script>

<template>
  <div
    ref="root"
    class="relative rounded-[var(--radius-lg)] border bg-[var(--c-surface)] px-4 py-3.5 shadow-[var(--shadow-xs)]"
    :title="tooltip || undefined"
    @keydown="onInfoKeydown"
    @focusout="onFocusOut"
  >
    <div class="flex items-start justify-between gap-2">
      <p class="text-xs font-medium tracking-wide text-[var(--c-text-muted)]">
        {{ label }}
      </p>
      <button
        v-if="tooltip"
        ref="infoButton"
        type="button"
        class="-mt-0.5 -mr-1.5 grid size-5 shrink-0 place-items-center rounded-full text-2xs font-semibold text-[var(--c-text-muted)] hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-text)]"
        :aria-expanded="infoOpen"
        :aria-controls="infoId"
        :aria-label="`About ${label}`"
        @click="infoOpen = !infoOpen"
      >
        i
      </button>
    </div>

    <div
      v-if="infoOpen"
      :id="infoId"
      title=""
      class="absolute inset-x-2 top-9 z-30 rounded-[var(--radius-md)] border bg-[var(--c-surface-raised)] px-3 py-2 text-xs whitespace-pre-line text-[var(--c-text-secondary)] shadow-[var(--shadow-md)] origin-top-right transition-[opacity,scale] duration-(--duration-popover) ease-out starting:opacity-0 motion-safe:starting:scale-[0.97]"
    >
      {{ tooltip }}
    </div>

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
