<script setup lang="ts">
/**
 * The dashboard's view controls: count unit, unknown devices, date range, and the filter chips.
 *
 * Shown only where they mean something (ADR-016 §8): the count unit when some widget counts by it,
 * the range when some widget has a time axis or a window. A control that changes nothing on the
 * screen teaches people that controls do not work. Each widget that cannot apply the view says so on
 * its own card.
 *
 * Everything here writes the URL - reload and Back reproduce the view - never the saved dashboard.
 */
import { computed } from 'vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import Button from '@/design-system/Button.vue'
import { CONTRACT } from '../registry/contract'
import { rangeLabel, supportsCountBy, supportsRange } from '../composables/lens'
import { LENS_FILTER_KEYS, LENS_FILTER_LABELS, type LensFilterKey } from '../composables/useDashboardLens'
import type { CountBy, DashboardLens, WidgetInstance } from '../types/definition'
import { typeSpec } from '../registry/contract'

const props = defineProps<{
  widgets: WidgetInstance[]
  /** The saved defaults with the URL on top: what is in force. */
  view: DashboardLens
  defaults: DashboardLens
}>()

const emit = defineEmits<{
  countBy: [value: CountBy]
  includeUnknown: [value: boolean]
  range: [value: string]
  removeFilter: [key: LensFilterKey]
  clearFilters: []
}>()

const specs = computed(() => props.widgets.map((w) => typeSpec(w.type)).filter((s) => !!s))

const showCountBy = computed(() => specs.value.some((s) => supportsCountBy(s!)))
const showUnknown = computed(() => specs.value.some((s) => s!.filters.includes('includeUnknownDevice')))
const showRange = computed(() => specs.value.some((s) => supportsRange(s!)))

const COUNT_BY_OPTIONS = [
  { value: 'bindings' as const, label: 'Bindings' },
  { value: 'subscribers' as const, label: 'Subscribers' },
  { value: 'handsets' as const, label: 'Handsets' },
]

const RANGE_OPTIONS = CONTRACT.lens.ranges

const countBy = computed<CountBy>(() => props.view.countBy ?? 'bindings')
const includeUnknown = computed(() => props.view.includeUnknownDevice ?? true)
const range = computed(() => props.view.range ?? 'all')
const customRange = computed(() => !RANGE_OPTIONS.includes(range.value))

const chips = computed(() =>
  LENS_FILTER_KEYS.filter((key) => props.view[key]).map((key) => ({
    key,
    value: props.view[key]!,
    fromDefaults: props.defaults[key] === props.view[key],
  })),
)

function onRange(event: Event) {
  emit('range', (event.target as HTMLSelectElement).value)
}
</script>

<template>
  <div v-if="showCountBy || showUnknown || showRange || chips.length" class="flex flex-col gap-2">
    <div class="flex flex-wrap items-center gap-2">
      <!-- A segmented control rather than a dropdown: three options people switch between
           constantly, and showing all three keeps the distinction present (the old page's choice). -->
      <SegmentedControl
        v-if="showCountBy"
        :model-value="countBy"
        :options="COUNT_BY_OPTIONS"
        label="Count breakdowns by"
        @update:model-value="emit('countBy', $event)"
      />

      <Button v-if="showUnknown" size="sm" :aria-pressed="!includeUnknown" @click="emit('includeUnknown', !includeUnknown)">
        {{ includeUnknown ? 'Hide unknown devices' : 'Show unknown devices' }}
      </Button>

      <label v-if="showRange" class="inline-flex items-center gap-1.5 text-xs text-[var(--c-text-secondary)]">
        <span>Range</span>
        <select
          class="h-7 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-xs text-[var(--c-text)]"
          :value="range"
          @change="onRange"
        >
          <option v-for="r in RANGE_OPTIONS" :key="r" :value="r">{{ rangeLabel(r) }}</option>
          <option v-if="customRange" :value="range">{{ rangeLabel(range) }}</option>
        </select>
      </label>
      <span v-if="showRange && range !== 'all'" class="text-2xs text-[var(--c-text-muted)]">
        counted back from the latest day in the data, not from today
      </span>
    </div>

    <div v-if="chips.length" class="flex flex-wrap items-center gap-2">
      <span class="text-xs text-[var(--c-text-muted)]">Filtered by</span>
      <template v-for="chip in chips" :key="chip.key">
        <!-- A filter the dashboard itself sets is changed in edit mode, not removed from the URL. -->
        <span
          v-if="chip.fromDefaults"
          class="inline-flex items-center gap-1.5 rounded-full border py-1 pr-2.5 pl-2.5 text-xs font-medium"
          title="Set by this dashboard; change it in Edit, under Dashboard settings."
        >
          <span class="text-[var(--c-text-secondary)]">{{ LENS_FILTER_LABELS[chip.key] }}:</span>
          <span>{{ chip.value }}</span>
          <span class="text-2xs font-normal text-[var(--c-text-muted)]">dashboard default</span>
        </span>
        <button
          v-else
          type="button"
          class="group inline-flex items-center gap-1.5 rounded-full bg-[var(--c-accent-subtle)] py-1 pr-2 pl-2.5 text-xs font-medium"
          :title="`Remove the ${LENS_FILTER_LABELS[chip.key]} filter`"
          @click="emit('removeFilter', chip.key)"
        >
          <span class="text-[var(--c-text-secondary)]">{{ LENS_FILTER_LABELS[chip.key] }}:</span>
          <span>{{ chip.value }}</span>
          <span aria-hidden="true" class="text-[var(--c-text-muted)] group-hover:text-[var(--c-text)]">×</span>
        </button>
      </template>
      <Button v-if="chips.some((c) => !c.fromDefaults)" variant="ghost" size="sm" @click="emit('clearFilters')">Clear all</Button>
      <span class="text-2xs text-[var(--c-text-muted)]">· filtered views query raw data and take a few seconds</span>
    </div>
  </div>
</template>
