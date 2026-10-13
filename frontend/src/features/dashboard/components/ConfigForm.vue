<script setup lang="ts">
/**
 * Every widget's settings, drawn from two sources: the contract says what a field is - its kind,
 * bounds and allowed values, the same ones the server enforces - and the widget's definition says
 * how to present it (label, help, section, value labels). One form for 33 types; a widget brings its
 * own component only for what this cannot draw (an Explorer query, a pair of dates).
 *
 * Sections: General (title, subtitle), Data & filters (what is fetched, and the view the widget sets
 * for itself), Display (how it is drawn - changes redraw at once, no new request), Advanced only when
 * a type has such fields.
 */
import { computed, reactive, useId } from 'vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import TextField from '@/design-system/TextField.vue'
import Toggle from '@/design-system/Toggle.vue'
import { useAuth } from '@/features/auth/useAuth'
import { CONTRACT, type ConfigFieldSpec } from '../registry/contract'
import type { ResolvedWidgetDefinition, FieldError, FieldSection, FieldPresentation } from '../types/widget'
import type { CountBy, DashboardLens, WidgetConfig, WidgetInstance } from '../types/definition'
import { rangeLabel, supportsCountBy, supportsRange } from '../composables/lens'

const props = defineProps<{
  definition: ResolvedWidgetDefinition
  config: WidgetConfig
  title: string
  subtitle: string
  overrides: DashboardLens
  /** The dashboard's view, so "use the dashboard's" can say what that is. */
  dashboardView: DashboardLens
  errors: FieldError[]
  /** A stand-in instance for a custom settings component. */
  instance: WidgetInstance
}>()

const emit = defineEmits<{
  'update:config': [config: WidgetConfig]
  'update:title': [title: string]
  'update:subtitle': [subtitle: string]
  'update:overrides': [overrides: DashboardLens]
}>()

const { can } = useAuth()
const formId = useId()
const spec = computed(() => props.definition.spec)

const SECTION_TITLES: Record<FieldSection, string> = {
  general: 'General',
  data: 'Data & filters',
  display: 'Display',
  advanced: 'Advanced',
}

interface Row {
  name: string
  field: ConfigFieldSpec
  presentation: FieldPresentation
}

const rows = computed(() => {
  const custom = new Set<string>(props.definition.customFields ?? [])
  const bySection: Record<FieldSection, Row[]> = { general: [], data: [], display: [], advanced: [] }
  for (const [name, field] of Object.entries(spec.value.config)) {
    if (custom.has(name)) continue
    const presentation = (props.definition.fields as Record<string, FieldPresentation | undefined>)[name] ?? {
      label: name,
      section: 'data' as const,
    }
    if (presentation.visibleWhen && !presentation.visibleWhen(props.config)) continue
    // A contract field placed in "general" (a lookup box's target) is drawn first in the data section by
    // the same renderer as every other field: the general fieldset holds only the title and subtitle.
    const section = presentation.section === 'general' ? 'data' : presentation.section
    if (presentation.section === 'general') bySection.data.unshift({ name, field, presentation })
    else bySection[section].push({ name, field, presentation })
  }
  return bySection
})

function errorFor(name: string) {
  return props.errors.find((e) => e.field === name)?.message ?? null
}

function set(name: string, value: unknown) {
  emit('update:config', { ...props.config, [name]: value })
}

/** For a field whose value decides a permission (a risk list's rule), only what the person may choose. */
function allowedValues(name: string, values: string[]): string[] {
  const byConfig = spec.value.permissionsByConfig
  if (!byConfig || byConfig.field !== name) return values
  return values.filter((v) => (byConfig.map[v] ?? []).every((p) => can(p)))
}

function options(row: Row) {
  const values = allowedValues(row.name, row.field.values ?? [])
  const labels = row.presentation.options ?? {}
  const ordered = Object.keys(labels).filter((v) => values.includes(v))
  const rest = values.filter((v) => !ordered.includes(v))
  return [...ordered, ...rest].map((value) => ({ value, label: labels[value] ?? value }))
}

function toggleListValue(row: Row, value: string, on: boolean) {
  const current = Array.isArray(props.config[row.name]) ? (props.config[row.name] as string[]) : []
  const next = on ? [...current, value] : current.filter((v) => v !== value)
  // Keep the contract's order, so two equal choices are equal configs.
  set(row.name, (row.field.values ?? []).filter((v) => next.includes(v)))
}

/**
 * A list of free-text values (a risk list's device types), typed as one comma-separated line. What was
 * typed is kept while it still means the saved list, so a comma or a space being typed is not erased.
 */
const listText = reactive<Record<string, string>>({})

function parseList(raw: string): string[] {
  return raw
    .split(',')
    .map((v) => v.trim())
    .filter((v) => v !== '')
}

function listValue(name: string): string {
  const current = Array.isArray(props.config[name]) ? (props.config[name] as string[]) : []
  const typed = listText[name]
  return typed !== undefined && JSON.stringify(parseList(typed)) === JSON.stringify(current) ? typed : current.join(', ')
}

function onList(name: string, raw: string) {
  listText[name] = raw
  set(name, parseList(raw))
}

function onInt(row: Row, raw: string) {
  // Emptied while typing a new number: stays empty - writing the default back into the box made it
  // impossible to clear (typing 25 after clearing 5 gave 1025). A required field left empty is
  // reported by validateConfig like any other bad value.
  if (raw.trim() === '') return set(row.name, row.field.nullable ? null : '')
  const n = Number(raw)
  set(row.name, Number.isFinite(n) ? n : raw)
}

// ---------------------------------------------------------------- overrides

const countBy = computed(() => supportsCountBy(spec.value))
const range = computed(() => supportsRange(spec.value))
const textFilters = computed(() =>
  (['vendor', 'deviceType', 'operatingSystem', 'tac'] as const).filter((k) => spec.value.filters.includes(k)),
)
const unknown = computed(() => spec.value.filters.includes('includeUnknownDevice'))
const hasView = computed(() => countBy.value || range.value || textFilters.value.length > 0 || unknown.value)

const FILTER_LABELS = { vendor: 'Vendor', deviceType: 'Device type', operatingSystem: 'Operating system', tac: 'TAC' } as const

function setOverride<K extends keyof DashboardLens>(key: K, value: DashboardLens[K] | undefined) {
  const next = { ...props.overrides }
  if (value === undefined || value === '' || value === null) delete next[key]
  else next[key] = value
  emit('update:overrides', next)
}

function onOverrideSelect(key: 'countBy' | 'range' | 'includeUnknownDevice', raw: string) {
  if (raw === '') return setOverride(key, undefined)
  if (key === 'includeUnknownDevice') return setOverride(key, raw === 'show')
  if (key === 'countBy') return setOverride(key, raw as CountBy)
  setOverride(key, raw)
}
</script>

<template>
  <div class="flex flex-col gap-5">
    <!-- General -->
    <fieldset class="flex flex-col gap-3">
      <legend class="mb-2 text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">{{ SECTION_TITLES.general }}</legend>
      <TextField
        :model-value="title"
        label="Title"
        :hint="`Shown on the card. Leave empty for “${definition.name}”.`"
        :placeholder="definition.name"
        autofocus
        :error="title.length > CONTRACT.limits.titleMaxLength ? `At most ${CONTRACT.limits.titleMaxLength} characters.` : null"
        @update:model-value="emit('update:title', $event)"
      />
      <TextField
        :model-value="subtitle"
        label="Subtitle"
        optional-note="optional"
        :error="subtitle.length > CONTRACT.limits.subtitleMaxLength ? `At most ${CONTRACT.limits.subtitleMaxLength} characters.` : null"
        @update:model-value="emit('update:subtitle', $event)"
      />
    </fieldset>

    <template v-for="section in (['data', 'display', 'advanced'] as const)" :key="section">
      <fieldset v-if="rows[section].length || (section === 'data' && (hasView || definition.settings))" class="flex flex-col gap-3">
        <legend class="mb-2 text-xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">{{ SECTION_TITLES[section] }}</legend>

        <div v-for="row in rows[section]" :key="row.name" class="flex flex-col gap-1.5">
          <!-- enum: a segmented control for up to five choices, otherwise a list -->
          <template v-if="row.field.kind === 'enum'">
            <span :id="`${formId}-${row.name}`" class="text-xs font-medium text-[var(--c-text-secondary)]">{{ row.presentation.label }}</span>
            <SegmentedControl
              v-if="options(row).length <= 5"
              :model-value="String(config[row.name] ?? '')"
              :options="options(row)"
              :label="row.presentation.label"
              @update:model-value="set(row.name, $event)"
            />
            <select
              v-else
              :aria-labelledby="`${formId}-${row.name}`"
              class="h-8 max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="config[row.name]"
              @change="set(row.name, ($event.target as HTMLSelectElement).value)"
            >
              <option v-for="o in options(row)" :key="o.value" :value="o.value">{{ o.label }}</option>
            </select>
          </template>

          <!-- enumList: checkboxes -->
          <template v-else-if="row.field.kind === 'enumList'">
            <fieldset class="flex flex-col gap-1">
              <legend class="text-xs font-medium text-[var(--c-text-secondary)]">{{ row.presentation.label }}</legend>
              <div class="flex flex-wrap gap-x-4 gap-y-1">
                <label v-for="o in options(row)" :key="o.value" class="inline-flex items-center gap-1.5 text-sm">
                  <input
                    type="checkbox"
                    class="size-4 accent-[var(--c-accent)]"
                    :checked="Array.isArray(config[row.name]) && (config[row.name] as string[]).includes(o.value)"
                    @change="toggleListValue(row, o.value, ($event.target as HTMLInputElement).checked)"
                  />
                  {{ o.label }}
                </label>
              </div>
            </fieldset>
          </template>

          <!-- int -->
          <template v-else-if="row.field.kind === 'int'">
            <label :for="`${formId}-${row.name}`" class="text-xs font-medium text-[var(--c-text-secondary)]">{{ row.presentation.label }}</label>
            <input
              :id="`${formId}-${row.name}`"
              type="number"
              inputmode="numeric"
              :min="row.field.min"
              :max="row.field.max"
              step="1"
              class="h-8 w-32 max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm tabular"
              :value="config[row.name] ?? ''"
              :aria-invalid="!!errorFor(row.name)"
              :placeholder="row.field.nullable ? 'Not set' : undefined"
              @input="onInt(row, ($event.target as HTMLInputElement).value)"
            />
          </template>

          <!-- bool -->
          <Toggle
            v-else-if="row.field.kind === 'bool'"
            :model-value="config[row.name] === true"
            :label="row.presentation.label"
            @update:model-value="set(row.name, $event)"
          />

          <!-- string -->
          <TextField
            v-else-if="row.field.kind === 'string'"
            :model-value="String(config[row.name] ?? '')"
            :label="row.presentation.label"
            :error="errorFor(row.name)"
            @update:model-value="set(row.name, $event)"
          />

          <!-- stringList: one line, comma-separated -->
          <TextField
            v-else-if="row.field.kind === 'stringList'"
            :model-value="listValue(row.name)"
            :label="row.presentation.label"
            placeholder="All"
            :error="errorFor(row.name)"
            @update:model-value="onList(row.name, $event)"
          />

          <!-- date -->
          <template v-else-if="row.field.kind === 'date'">
            <label :for="`${formId}-${row.name}`" class="text-xs font-medium text-[var(--c-text-secondary)]">{{ row.presentation.label }}</label>
            <input
              :id="`${formId}-${row.name}`"
              type="date"
              class="h-8 w-44 max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="config[row.name] ?? ''"
              @input="set(row.name, ($event.target as HTMLInputElement).value || null)"
            />
          </template>

          <!-- range -->
          <template v-else-if="row.field.kind === 'range'">
            <label :for="`${formId}-${row.name}`" class="text-xs font-medium text-[var(--c-text-secondary)]">{{ row.presentation.label }}</label>
            <select
              :id="`${formId}-${row.name}`"
              class="h-8 max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="config[row.name]"
              @change="set(row.name, ($event.target as HTMLSelectElement).value)"
            >
              <option v-for="r in CONTRACT.lens.ranges" :key="r" :value="r">{{ rangeLabel(r) }}</option>
            </select>
          </template>

          <p v-if="row.presentation.help" class="text-2xs text-[var(--c-text-muted)]">{{ row.presentation.help }}</p>
          <p v-if="errorFor(row.name) && row.field.kind !== 'string' && row.field.kind !== 'stringList'" class="text-xs text-[var(--c-danger-text)]" role="alert">
            {{ errorFor(row.name) }}
          </p>
        </div>

        <!-- The widget's own form, for what the generic one cannot draw -->
        <component
          :is="definition.settings"
          v-if="section === 'data' && definition.settings"
          :config="config"
          :instance="instance"
          :spec="spec"
          @update:config="emit('update:config', $event)"
        />

        <!-- The view this widget sets for itself -->
        <div v-if="section === 'data' && hasView" class="flex flex-col gap-2 rounded-[var(--radius-md)] border px-3 py-2.5">
          <p class="text-xs font-medium text-[var(--c-text-secondary)]">View</p>
          <p class="text-2xs text-[var(--c-text-muted)]">
            By default a widget follows the dashboard. Set one here only when this widget should differ; the card then says so.
          </p>

          <label v-if="countBy" class="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span>Count by</span>
            <select
              class="h-8 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="overrides.countBy ?? ''"
              @change="onOverrideSelect('countBy', ($event.target as HTMLSelectElement).value)"
            >
              <option value="">The dashboard's ({{ dashboardView.countBy ?? 'bindings' }})</option>
              <option v-for="m in spec.measures" :key="m" :value="m">{{ m }}</option>
            </select>
          </label>

          <label v-if="range" class="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span>Range</span>
            <select
              class="h-8 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="overrides.range ?? ''"
              @change="onOverrideSelect('range', ($event.target as HTMLSelectElement).value)"
            >
              <option value="">The dashboard's ({{ rangeLabel(dashboardView.range) }})</option>
              <option v-for="r in CONTRACT.lens.ranges" :key="r" :value="r">{{ rangeLabel(r) }}</option>
            </select>
          </label>

          <label v-if="unknown" class="flex flex-wrap items-center justify-between gap-2 text-sm">
            <span>Unknown devices</span>
            <select
              class="h-8 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm"
              :value="overrides.includeUnknownDevice === undefined ? '' : overrides.includeUnknownDevice ? 'show' : 'hide'"
              @change="onOverrideSelect('includeUnknownDevice', ($event.target as HTMLSelectElement).value)"
            >
              <option value="">The dashboard's</option>
              <option value="show">Show</option>
              <option value="hide">Hide</option>
            </select>
          </label>

          <TextField
            v-for="key in textFilters"
            :key="key"
            :model-value="overrides[key] ?? ''"
            :label="FILTER_LABELS[key]"
            :placeholder="dashboardView[key] ? `The dashboard's (${dashboardView[key]})` : 'The dashboard\'s (none)'"
            :hint="key === 'tac' ? 'Eight digits - a device model, never a person.' : undefined"
            :error="errorFor(`overrides.${key}`)"
            @update:model-value="setOverride(key, $event.trim() || undefined)"
          />
        </div>
      </fieldset>
    </template>
  </div>
</template>
