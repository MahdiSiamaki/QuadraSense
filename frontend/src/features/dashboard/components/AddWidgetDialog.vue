<script setup lang="ts">
/**
 * Add widget, and Configure: one dialog, two steps (ADR-016 §6).
 *
 * Select: groups on the left with their counts, the catalogue in the middle, searched locally on
 * every keystroke (name, description, group, tags, aliases - "SIM swap" finds the SIM-change
 * widgets), and on the right what the selected widget is: its unit, its permissions, its time scope,
 * which filters it honours. Nothing on this step queries data; a widget the person may not use
 * cannot be chosen and says why.
 *
 * Configure: the form beside a live preview that is the widget itself - the same component and the
 * same data hooks the dashboard uses, not a look-alike. A display change redraws at once; a data
 * change is applied after half a second and only when valid, and a superseded request is cancelled
 * (TanStack Query aborts a query nobody observes any more). Risk lists and Explorer queries preview
 * their structure at once and run only on the preview's own Run button.
 *
 * Back keeps what was typed. Closing a changed form asks first, inside the dialog - no dialog on top
 * of a dialog. Enter in the search field never moves on, and an invalid form never adds.
 */
import { computed, nextTick, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import Modal from '@/design-system/Modal.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import { useAuth } from '@/features/auth/useAuth'
import { catalogEntries, catalogGroups, getWidget, type CatalogEntry } from '../registry'
import { CONTRACT, requiredPermissions, typeSpec } from '../registry/contract'
import { normalizeConfig, validateConfig } from '../registry/validate'
import { effectiveLens, supportsCountBy, supportsRange } from '../composables/lens'
import type { DashboardLens, WidgetConfig, WidgetInstance } from '../types/definition'
import type { WidgetGroupId } from '../registry/contract'
import ConfigForm from './ConfigForm.vue'
import WidgetFrame from './WidgetFrame.vue'
import WidgetErrorBoundary from './WidgetErrorBoundary.vue'
import WidgetAbout from './WidgetAbout.vue'
import WidgetIcon from './WidgetIcon.vue'

export interface WizardResult {
  /** Set when an existing widget was configured. */
  id?: string
  type: string
  config: WidgetConfig
  title: string | null
  subtitle: string | null
  overrides: DashboardLens
}

const props = defineProps<{
  open: boolean
  /** The widget being configured; null to add a new one. */
  widget: WidgetInstance | null
  defaults: DashboardLens
  urlLens: DashboardLens
  widgetCount: number
  maxWidgets: number
}>()

const emit = defineEmits<{ close: []; done: [result: WizardResult] }>()

const { can } = useAuth()

type Step = 'select' | 'configure'
const step = ref<Step>('select')
const group = ref<WidgetGroupId | 'all'>('all')
const query = ref('')
const selectedKey = ref<string | null>(null)

const form = ref<{ type: string; config: WidgetConfig; title: string; subtitle: string; overrides: DashboardLens } | null>(null)
const initial = ref('')
const confirmingClose = ref(false)
const keepEditing = ref<InstanceType<typeof Button> | null>(null)
const cancelButton = ref<InstanceType<typeof Button> | null>(null)

/** Back from the confirmation to the form, with focus where it was: on Cancel. */
function keepEditingSettings() {
  confirmingClose.value = false
  void nextTick(() => (cancelButton.value?.$el as HTMLElement | undefined)?.focus())
}
const mobileTab = ref<'settings' | 'preview'>('settings')

const entries = catalogEntries()

/**
 * The config a new widget starts from. For a type whose permissions follow a config value - a lookup
 * box's target - the plain type starts on a value the person may use, so someone who may search IMSIs
 * but not subscribers can still add one. A preset keeps what it chose: its value is its meaning.
 */
function startingConfig(entry: Pick<CatalogEntry, 'type' | 'config' | 'presetKey'>): WidgetConfig {
  const byConfig = typeSpec(entry.type)?.permissionsByConfig
  if (!byConfig || entry.presetKey !== null) return entry.config
  const current = String(entry.config[byConfig.field] ?? '')
  if ((byConfig.map[current] ?? []).every((p) => can(p))) return entry.config
  const allowed = Object.keys(byConfig.map).find((value) => byConfig.map[value]!.every((p) => can(p)))
  return allowed === undefined ? entry.config : { ...entry.config, [byConfig.field]: allowed }
}

function missingFor(entry: Pick<CatalogEntry, 'type' | 'config' | 'presetKey'>) {
  return requiredPermissions(entry.type, startingConfig(entry)).filter((p) => !can(p))
}

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    confirmingClose.value = false
    mobileTab.value = 'settings'
    query.value = ''
    group.value = 'all'
    if (props.widget) {
      const w = props.widget
      form.value = {
        type: w.type,
        config: normalizeConfig(w.type, w.config),
        title: w.title ?? '',
        subtitle: w.subtitle ?? '',
        overrides: { ...(w.overrides ?? {}) },
      }
      selectedKey.value = w.type
      step.value = 'configure'
    } else {
      form.value = null
      selectedKey.value = null
      step.value = 'select'
    }
    initial.value = JSON.stringify(form.value)
    await nextTick()
    if (step.value === 'select') searchInput.value?.focus()
    else focusFirstField()
  },
)

// ------------------------------------------------------------------- select

const searchInput = ref<HTMLInputElement | null>(null)

const groupNames = Object.fromEntries(CONTRACT.groups.map((g) => [g.id, g.name])) as Record<WidgetGroupId, string>

function matches(entry: CatalogEntry, q: string) {
  const haystack = [entry.name, entry.description, groupNames[entry.group], ...entry.tags, ...entry.aliases].join(' ').toLowerCase()
  return q
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((token) => haystack.includes(token))
}

const searched = computed(() => entries.filter((e) => matches(e, query.value)))
const visible = computed(() => searched.value.filter((e) => group.value === 'all' || e.group === group.value))
const groups = computed(() => catalogGroups(searched.value))
const selected = computed(() => entries.find((e) => e.key === selectedKey.value) ?? null)
const selectedDefinition = computed(() => (selected.value ? getWidget(selected.value.type) : undefined))
const selectedMissing = computed(() => (selected.value ? missingFor(selected.value) : []))
const selectedNeeds = computed(() => (selected.value ? requiredPermissions(selected.value.type, startingConfig(selected.value)) : []))
const full = computed(() => !props.widget && props.widgetCount >= props.maxWidgets)

function select(entry: CatalogEntry) {
  selectedKey.value = entry.key
}

function describeFilters(type: string): string {
  const spec = typeSpec(type)
  if (!spec) return ''
  const parts: string[] = []
  if (spec.filters.some((f) => f !== 'includeUnknownDevice')) parts.push('Honours the dashboard filters (vendor, device type, OS, TAC).')
  else parts.push('Network-wide: the dashboard filters do not apply.')
  if (supportsCountBy(spec)) parts.push('Can count by bindings, subscribers or handsets.')
  if (supportsRange(spec)) parts.push('Follows the date range.')
  else if (spec.time === 'fixedWindow') parts.push('Uses each rule\'s own fixed window; the date range does not apply.')
  else if (spec.time === 'current') parts.push('A current-population figure; the date range does not apply.')
  if (spec.execution === 'explicit') parts.push('Runs only when you press Run: each run is audited.')
  return parts.join(' ')
}

function goConfigure() {
  const entry = selected.value
  if (!entry || selectedMissing.value.length || full.value) return
  const keep = form.value && form.value.type === entry.type && selectedKey.value === entry.key
  if (!keep) {
    form.value = {
      type: entry.type,
      config: normalizeConfig(entry.type, structuredClone(startingConfig(entry))),
      title: entry.title ?? '',
      subtitle: '',
      overrides: {},
    }
    initial.value = JSON.stringify({ type: entry.type })
  }
  step.value = 'configure'
  mobileTab.value = 'settings'
  void nextTick(focusFirstField)
}

// ---------------------------------------------------------------- configure

const configColumn = ref<HTMLElement | null>(null)

function focusFirstField() {
  configColumn.value?.querySelector<HTMLElement>('input, select, textarea')?.focus()
}

const definition = computed(() => (form.value ? getWidget(form.value.type) : undefined))
const errors = computed(() => (form.value ? validateConfig(form.value.type, form.value.config) : []))
const titleTooLong = computed(() => (form.value?.title.length ?? 0) > CONTRACT.limits.titleMaxLength || (form.value?.subtitle.length ?? 0) > CONTRACT.limits.subtitleMaxLength)
const semanticErrors = computed(() => (form.value && definition.value?.validate ? definition.value.validate(form.value.config) : []))
const allErrors = computed(() => [...errors.value, ...semanticErrors.value])
const valid = computed(() => !!form.value && allErrors.value.length === 0 && !titleTooLong.value)
const dirty = computed(() => JSON.stringify(form.value) !== initial.value)

/** Fields whose change only redraws; everything else is data and waits for the debounce. */
const visualFields = computed(() => {
  const d = definition.value
  if (!d) return new Set<string>()
  return new Set(Object.entries(d.fields).filter(([, p]) => p?.visual).map(([name]) => name))
})

function dataPart(config: WidgetConfig) {
  return Object.fromEntries(Object.entries(config).filter(([k]) => !visualFields.value.has(k)))
}

/** The data settings the preview runs with: valid ones, half a second after the last change. */
const appliedData = ref<WidgetConfig>({})
const appliedOverrides = ref<DashboardLens>({})
let debounce: ReturnType<typeof setTimeout> | null = null

watch(
  () => (form.value ? JSON.stringify([dataPart(form.value.config), form.value.overrides]) : ''),
  (now, before) => {
    if (!form.value) return
    if (debounce) clearTimeout(debounce)
    const apply = () => {
      if (!form.value || errors.value.length) return
      appliedData.value = structuredClone(dataPart(form.value.config))
      appliedOverrides.value = { ...form.value.overrides }
    }
    // The first valid config of a new type applies at once; later edits wait.
    if (!before) apply()
    else debounce = setTimeout(apply, 500)
    void now
  },
  { immediate: true },
)

const previewInstance = computed<WidgetInstance | null>(() => {
  const f = form.value
  const spec = f && typeSpec(f.type)
  if (!f || !spec) return null
  const visual = Object.fromEntries(Object.entries(f.config).filter(([k]) => visualFields.value.has(k)))
  return {
    id: props.widget?.id ?? 'w_preview00',
    type: f.type,
    configVersion: spec.configVersion,
    title: f.title.trim() || null,
    subtitle: f.subtitle.trim() || null,
    config: { ...f.config, ...appliedData.value, ...visual },
    overrides: appliedOverrides.value,
    layout: { x: 0, y: 0, w: spec.size.w, h: spec.size.h },
  }
})

const previewLens = computed(() => {
  const spec = form.value && typeSpec(form.value.type)
  return spec ? effectiveLens(spec, props.defaults, props.urlLens, appliedOverrides.value) : {}
})

const previewHeight = computed(() => {
  const spec = form.value && typeSpec(form.value.type)
  return `${Math.min(30, Math.max(12, (spec?.size.h ?? 6) * 2.5))}rem`
})

const dashboardView = computed(() => ({ ...props.defaults, ...props.urlLens }))

function finish() {
  if (!form.value || !valid.value) return
  emit('done', {
    id: props.widget?.id,
    type: form.value.type,
    config: normalizeConfig(form.value.type, form.value.config),
    title: form.value.title.trim() || null,
    subtitle: form.value.subtitle.trim() || null,
    overrides: form.value.overrides,
  })
}

function onSubmit() {
  if (step.value === 'select') goConfigure()
  else finish()
}

function back() {
  step.value = 'select'
  void nextTick(() => searchInput.value?.focus())
}

function requestClose() {
  if (dirty.value && form.value && !confirmingClose.value) {
    confirmingClose.value = true
    // The Cancel that asked is gone from the footer now, and autofocus does nothing in a dialog that is
    // already open: focus moves to the safe answer by hand, and the question is announced.
    void nextTick(() => (keepEditing.value?.$el as HTMLElement | undefined)?.focus())
    return
  }
  confirmingClose.value = false
  emit('close')
}
</script>

<template>
  <Modal
    :open="open"
    size="xl"
    fill
    :title="widget ? `Configure ${widget.title ?? definition?.name ?? 'widget'}` : step === 'select' ? 'Add widget' : `Add widget: ${definition?.name ?? ''}`"
    :description="step === 'select' ? 'Choose what to show. Every widget reads only the data you are allowed to see.' : 'Set it up; the preview is the widget itself, on real data.'"
    @close="requestClose"
    @submit="onSubmit"
  >
    <!-- ---------------------------------------------------------- select -->
    <div v-if="step === 'select'" class="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[12rem_minmax(0,1fr)] lg:grid-cols-[12rem_minmax(0,1fr)_20rem]">
      <nav aria-label="Widget groups" class="hidden min-h-0 overflow-y-auto border-r p-2 md:block">
        <button
          type="button"
          class="flex w-full items-center justify-between rounded-[var(--radius-md)] px-2 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)]"
          :class="group === 'all' ? 'bg-[var(--c-accent-subtle)] font-medium' : ''"
          :aria-current="group === 'all' ? 'true' : undefined"
          @click="group = 'all'"
        >
          <span>All widgets</span><span class="text-2xs tabular text-[var(--c-text-muted)]">{{ searched.length }}</span>
        </button>
        <button
          v-for="g in groups"
          :key="g.id"
          type="button"
          class="mt-0.5 flex w-full items-center justify-between gap-2 rounded-[var(--radius-md)] px-2 py-1.5 text-left text-sm hover:bg-[var(--c-surface-hover)]"
          :class="group === g.id ? 'bg-[var(--c-accent-subtle)] font-medium' : ''"
          :aria-current="group === g.id ? 'true' : undefined"
          @click="group = g.id"
        >
          <span>{{ g.name }}</span><span class="text-2xs tabular text-[var(--c-text-muted)]">{{ g.count }}</span>
        </button>
      </nav>

      <div class="flex min-h-0 flex-col">
        <div class="flex flex-col gap-2 border-b p-3">
          <label for="widget-catalog-search" class="sr-only">Search widgets</label>
          <input
            id="widget-catalog-search"
            ref="searchInput"
            v-model="query"
            type="search"
            autocomplete="off"
            placeholder="Search: e.g. SIM swap, new models, freshness"
            class="w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2.5 py-1.5 text-sm"
            @keydown.enter.prevent
          />
          <select
            class="h-8 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm md:hidden"
            :value="group"
            aria-label="Widget group"
            @change="group = ($event.target as HTMLSelectElement).value as WidgetGroupId | 'all'"
          >
            <option value="all">All widgets ({{ searched.length }})</option>
            <option v-for="g in groups" :key="g.id" :value="g.id">{{ g.name }} ({{ g.count }})</option>
          </select>
          <p class="text-2xs text-[var(--c-text-muted)]" aria-live="polite">
            {{ group === 'all' ? 'All widgets' : groupNames[group] }} · {{ visible.length }} {{ visible.length === 1 ? 'widget' : 'widgets' }}
          </p>
        </div>

        <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain p-3">
          <ul v-if="visible.length" class="grid grid-cols-1 gap-2 sm:grid-cols-2" aria-label="Widgets">
            <li v-for="entry in visible" :key="entry.key">
              <button
                type="button"
                class="flex h-full w-full items-start gap-2.5 rounded-[var(--radius-md)] border px-3 py-2.5 text-left hover:bg-[var(--c-surface-hover)] disabled:cursor-not-allowed"
                :class="selectedKey === entry.key ? 'border-[var(--c-accent)] bg-[var(--c-accent-subtle)]' : ''"
                :aria-pressed="selectedKey === entry.key"
                @click="select(entry)"
                @dblclick="() => { select(entry); goConfigure() }"
              >
                <WidgetIcon :name="entry.icon" class="mt-0.5 text-[var(--c-text-secondary)]" />
                <span class="min-w-0 flex-1">
                  <span class="block text-sm font-medium">{{ entry.name }}</span>
                  <span class="mt-0.5 line-clamp-2 block text-xs text-[var(--c-text-muted)]">{{ entry.description }}</span>
                  <span v-if="missingFor(entry).length" class="mt-1 block text-2xs text-[var(--c-warning-text)]">
                    Needs {{ missingFor(entry).join(', ') }}
                  </span>
                </span>
              </button>
            </li>
          </ul>
          <div v-else class="flex flex-col items-center gap-2 py-10 text-center text-sm">
            <p>No widget matches “{{ query }}”{{ group === 'all' ? '' : ` in ${groupNames[group]}` }}.</p>
            <div class="flex gap-2">
              <Button v-if="query" size="sm" @click="query = ''">Clear search</Button>
              <Button v-if="group !== 'all'" size="sm" variant="ghost" @click="group = 'all'">Search all groups</Button>
            </div>
          </div>
        </div>
        <!-- Below 1024 px the side panel is not drawn; the same details are a disclosure here. -->
        <details v-if="selected && selectedDefinition" class="shrink-0 border-t px-3 py-2 lg:hidden">
          <summary class="cursor-pointer text-xs font-medium text-[var(--c-text-secondary)]">About “{{ selected.name }}”</summary>
          <p class="mt-2 text-xs text-[var(--c-text-secondary)]">{{ selected.description }}</p>
          <WidgetAbout
            class="mt-2 max-h-[40dvh] overflow-y-auto"
            :definition="selectedDefinition"
            :filters="describeFilters(selected.type)"
            :needs="selectedNeeds"
            :missing="selectedMissing"
          />
        </details>
      </div>

      <aside class="hidden min-h-0 overflow-y-auto border-l p-4 lg:block" aria-label="About the selected widget">
        <template v-if="selected && selectedDefinition">
          <div class="flex items-center gap-2">
            <WidgetIcon :name="selected.icon" />
            <h3 class="text-sm font-semibold">{{ selected.name }}</h3>
          </div>
          <p class="mt-1 text-xs text-[var(--c-text-secondary)]">{{ selected.description }}</p>
          <WidgetAbout
            class="mt-3"
            :definition="selectedDefinition"
            :filters="describeFilters(selected.type)"
            :needs="selectedNeeds"
            :missing="selectedMissing"
          />
        </template>
        <p v-else class="text-xs text-[var(--c-text-muted)]">Select a widget to see what it shows, how it counts and what it needs.</p>
      </aside>
    </div>

    <!-- ------------------------------------------------------- configure -->
    <div v-else-if="form && definition" class="flex min-h-0 flex-1 flex-col">
      <div class="border-b px-3 py-2 lg:hidden">
        <SegmentedControl
          v-model="mobileTab"
          :options="[{ value: 'settings', label: 'Settings' }, { value: 'preview', label: 'Preview' }]"
          label="Show"
        />
      </div>
      <div class="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div
          ref="configColumn"
          class="min-h-0 overflow-y-auto overscroll-contain p-4"
          :class="mobileTab === 'settings' ? '' : 'max-lg:hidden'"
        >
          <ConfigForm
            v-model:config="form.config"
            v-model:title="form.title"
            v-model:subtitle="form.subtitle"
            v-model:overrides="form.overrides"
            :definition="definition"
            :dashboard-view="dashboardView"
            :errors="allErrors"
            :instance="previewInstance!"
          />
        </div>
        <section
          class="min-h-0 overflow-y-auto border-l bg-[var(--c-surface-sunken)] p-4"
          :class="mobileTab === 'preview' ? '' : 'max-lg:hidden'"
          aria-label="Preview"
        >
          <div class="lg:sticky lg:top-0">
            <p class="mb-2 text-2xs font-semibold tracking-wide text-[var(--c-text-muted)] uppercase">Preview</p>
            <div v-if="previewInstance" :style="{ height: previewHeight }">
              <WidgetFrame
                :title="previewInstance.title ?? definition.name"
                :subtitle="previewInstance.subtitle"
                :chrome="definition.chrome ?? 'card'"
              >
                <WidgetErrorBoundary>
                  <component
                    :is="definition.component"
                    :instance="previewInstance"
                    :lens="previewLens"
                    mode="preview"
                    :snapshot="null"
                    :active="open && step === 'configure'"
                  />
                </WidgetErrorBoundary>
              </WidgetFrame>
            </div>
            <p v-if="errors.length" class="mt-2 text-xs text-[var(--c-warning-text)]">
              The preview shows the last valid settings until these are fixed.
            </p>
          </div>
        </section>
      </div>
    </div>

    <template #actions>
      <template v-if="confirmingClose">
        <span id="add-widget-discard" class="mr-auto text-sm" role="alert">Discard these settings?</span>
        <Button ref="keepEditing" variant="ghost" aria-describedby="add-widget-discard" @click="keepEditingSettings">Keep editing</Button>
        <Button variant="danger" @click="requestClose">Discard</Button>
      </template>
      <template v-else-if="step === 'select'">
        <span v-if="full" class="mr-auto text-xs text-[var(--c-warning-text)]">This dashboard already has {{ maxWidgets }} widgets, the most it can hold.</span>
        <Button variant="ghost" @click="requestClose">Cancel</Button>
        <Button type="submit" variant="primary" :disabled="!selected || selectedMissing.length > 0 || full">Continue</Button>
      </template>
      <template v-else>
        <Button v-if="!widget" variant="ghost" class="mr-auto" @click="back">Back</Button>
        <span v-if="!valid" class="mr-auto text-xs text-[var(--c-text-muted)]">Fix the highlighted settings to continue.</span>
        <Button ref="cancelButton" variant="ghost" @click="requestClose">Cancel</Button>
        <Button type="submit" variant="primary" :disabled="!valid">{{ widget ? 'Apply' : 'Add to dashboard' }}</Button>
      </template>
    </template>
  </Modal>
</template>
