<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Dimension } from '@/api/dashboard'
import {
  WIDGETS,
  DIMENSION_OPTIONS,
  SERIES_OPTIONS,
  type WidgetConfig,
  type SeriesName,
  type ChartStyle,
} from './widgets/registry'

/**
 * The chrome around every widget: title, edit affordances, and the config panel.
 *
 * Kept separate from the widgets themselves so a widget only has to render data.
 * Adding a widget to the product means writing a component and a registry entry —
 * never touching drag handles, remove buttons or the settings form.
 */
const props = defineProps<{
  type: string
  config: WidgetConfig
  isEditing: boolean
}>()

const emit = defineEmits<{
  remove: []
  duplicate: []
  update: [config: WidgetConfig]
}>()

const showSettings = ref(false)
const definition = computed(() => WIDGETS[props.type])

const title = computed(() => {
  if (props.config.title) return props.config.title
  if (props.config.dimension) {
    return DIMENSION_OPTIONS.find((o) => o.value === props.config.dimension)?.label ?? 'Breakdown'
  }
  if (props.config.series) {
    return SERIES_OPTIONS.find((o) => o.value === props.config.series)?.label ?? 'Series'
  }
  return definition.value?.name ?? 'Widget'
})

const can = (field: keyof WidgetConfig) => definition.value?.editable.includes(field) ?? false

function set<K extends keyof WidgetConfig>(key: K, value: WidgetConfig[K]) {
  emit('update', { ...props.config, [key]: value })
}

/** Composition only makes sense for a series whose parts sum to a whole. */
const styleOptions = computed<Array<{ value: ChartStyle; label: string }>>(() => {
  const base: Array<{ value: ChartStyle; label: string }> = [
    { value: 'bar', label: 'Bar chart' },
    { value: 'table', label: 'Table' },
  ]
  if (props.type === 'series' && props.config.series === 'deviceClass') {
    base.unshift({ value: 'composition', label: 'Composition bar' })
  }
  return base
})
</script>

<template>
  <section
    class="flex h-full flex-col overflow-hidden rounded-[var(--radius-lg)] border bg-[var(--c-surface)] shadow-[var(--shadow-xs)]"
    :class="isEditing ? 'ring-1 ring-[var(--c-accent)]/25' : ''"
  >
    <header class="flex shrink-0 items-center gap-2 border-b px-4 py-2.5">
      <!-- Only the header is a drag handle. Making the whole card draggable would
           fight every click inside a chart or table. -->
      <span
        v-if="isEditing"
        class="widget-drag-handle -ml-1 cursor-grab px-1 text-[var(--c-text-muted)] select-none active:cursor-grabbing"
        title="Drag to move"
        aria-hidden="true"
      >⠿</span>

      <h2 class="min-w-0 flex-1 truncate text-[var(--text-sm)] font-semibold">{{ title }}</h2>

      <div v-if="isEditing" class="flex shrink-0 items-center gap-1">
        <button
          v-if="definition?.editable.length"
          type="button"
          class="rounded-[var(--radius-sm)] px-2 py-1 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          :class="showSettings ? 'bg-[var(--c-surface-sunken)]' : ''"
          :aria-expanded="showSettings"
          @click="showSettings = !showSettings"
        >
          Settings
        </button>
        <button
          type="button"
          class="rounded-[var(--radius-sm)] px-2 py-1 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          title="Duplicate this widget"
          @click="emit('duplicate')"
        >
          Duplicate
        </button>
        <button
          type="button"
          class="rounded-[var(--radius-sm)] px-2 py-1 text-[var(--text-xs)] font-medium text-[var(--c-danger)] hover:bg-[var(--c-danger-subtle)]"
          title="Remove this widget"
          @click="emit('remove')"
        >
          Remove
        </button>
      </div>
    </header>

    <!-- Settings -->
    <div v-if="isEditing && showSettings" class="shrink-0 border-b bg-[var(--c-surface-sunken)] px-4 py-3">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label v-if="can('dimension')" class="block">
          <span class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">Group by</span>
          <select
            class="mt-1 w-full rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            :value="config.dimension"
            @change="set('dimension', ($event.target as HTMLSelectElement).value as Dimension)"
          >
            <option v-for="o in DIMENSION_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </label>

        <label v-if="can('series')" class="block">
          <span class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">Series</span>
          <select
            class="mt-1 w-full rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            :value="config.series"
            @change="set('series', ($event.target as HTMLSelectElement).value as SeriesName)"
          >
            <option v-for="o in SERIES_OPTIONS" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </label>

        <label v-if="can('style')" class="block">
          <span class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">Show as</span>
          <select
            class="mt-1 w-full rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            :value="config.style"
            @change="set('style', ($event.target as HTMLSelectElement).value as ChartStyle)"
          >
            <option v-for="o in styleOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </label>

        <label v-if="can('limit')" class="block">
          <span class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">Rows</span>
          <select
            class="mt-1 w-full rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            :value="config.limit"
            @change="set('limit', Number(($event.target as HTMLSelectElement).value))"
          >
            <option v-for="n in [5, 10, 15, 20, 30, 50]" :key="n" :value="n">{{ n }}</option>
          </select>
        </label>

        <label v-if="can('title')" class="block">
          <span class="text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)]">Title</span>
          <input
            class="mt-1 w-full rounded-[var(--radius-sm)] border bg-[var(--c-surface)] px-2 py-1.5 text-[var(--text-xs)]"
            :value="config.title ?? ''"
            :placeholder="title"
            maxlength="60"
            @input="set('title', ($event.target as HTMLInputElement).value || undefined)"
          />
        </label>

        <label v-if="can('excludeUnknown')" class="flex items-center gap-2 self-end pb-1.5">
          <input
            type="checkbox"
            class="size-3.5"
            :checked="config.excludeUnknown ?? false"
            @change="set('excludeUnknown', ($event.target as HTMLInputElement).checked)"
          />
          <span class="text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            Exclude unknown devices
          </span>
        </label>
      </div>
    </div>

    <div class="min-h-0 flex-1 overflow-auto p-4">
      <slot />
    </div>
  </section>
</template>
