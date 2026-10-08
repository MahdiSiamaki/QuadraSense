<script setup lang="ts">
import { computed } from 'vue'
import type { ExplorerAggregate, ExplorerField } from '@/api/explorer'
import Button from '@/design-system/Button.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import ConditionTree from './ConditionTree.vue'
import { MEASURE_OPERATORS, nextId, suggestMeasureName, type MeasureDraft, type QueryDraft } from './model'
import { control, iconButton, miniLabel, mono } from './ui'

/**
 * What a query returns: rows with chosen columns, or groups with measures - and then the order
 * and the page size.
 *
 * Measures count exactly (uniqExact on the server), and they need no lookup permission: how
 * many SIMs a handset has had reveals no SIM. Grouping by a number, SIM or handset does, because
 * the group key is the identifier.
 */
const props = defineProps<{
  draft: QueryDraft
  fields: ExplorerField[]
  allowed: (field: ExplorerField) => boolean
  maxDepth: number
  maxPageSize: number
  problems: Map<string, string[]>
}>()

const AGGREGATES: Array<{ value: ExplorerAggregate; label: string }> = [
  { value: 'Count', label: 'Count rows' },
  { value: 'CountDistinct', label: 'Count different' },
  { value: 'Min', label: 'Earliest' },
  { value: 'Max', label: 'Latest' },
]

const groupable = computed(() => props.fields.filter((f) => f.groupable))
const dates = computed(() => props.fields.filter((f) => f.type === 'Date'))
const label = (name: string) => props.fields.find((f) => f.name === name)?.label ?? name

function toggle(list: string[], name: string) {
  const i = list.indexOf(name)
  if (i >= 0) list.splice(i, 1)
  else list.push(name)
}

function otherNames(m: MeasureDraft): string[] {
  return [...props.fields.map((f) => f.name), ...props.draft.measures.filter((x) => x !== m).map((x) => x.name)]
}

/** Changes a measure, and renames it too while its name is still the one suggested for it. */
function change(m: MeasureDraft, patch: Partial<Pick<MeasureDraft, 'aggregate' | 'field' | 'activeOnly'>>) {
  const auto = m.name === suggestMeasureName(m, otherNames(m))
  Object.assign(m, patch)
  if (m.aggregate === 'Count') m.field = null
  if ((m.aggregate === 'Min' || m.aggregate === 'Max') && !dates.value.some((d) => d.name === m.field)) {
    m.field = dates.value[0]?.name ?? null
  }
  if (m.aggregate === 'CountDistinct' && !m.field) m.field = props.fields[0]?.name ?? null
  if (auto) m.name = suggestMeasureName(m, otherNames(m))
}

function addMeasure() {
  const m: MeasureDraft = { id: nextId(), name: '', aggregate: 'Count', field: null, activeOnly: false }
  m.name = suggestMeasureName(m, otherNames(m))
  props.draft.measures.push(m)
}

/** Having's fields are the query's measures: counts, or dates for Earliest and Latest. */
const measureFields = computed<ExplorerField[]>(() =>
  props.draft.measures
    .filter((m) => m.name.trim())
    .map((m) => ({
      name: m.name.trim(),
      label: m.name.trim(),
      type: m.aggregate === 'Min' || m.aggregate === 'Max' ? 'Date' : 'Number',
      operators: MEASURE_OPERATORS,
      groupable: false,
      permission: null,
      description: '',
      values: null,
    })),
)

/** What can be sorted by: the columns the query returns. */
const sortable = computed(() =>
  props.draft.grouped
    ? [...props.draft.groupBy.map((g) => ({ name: g, label: label(g) })), ...measureFields.value.map((m) => ({ name: m.name, label: m.name }))]
    : props.draft.columns.map((c) => ({ name: c, label: label(c) })),
)

function addSort() {
  const used = new Set(props.draft.sort.map((s) => s.field))
  const next = sortable.value.find((s) => !used.has(s.name))
  if (next) props.draft.sort.push({ field: next.name, descending: false })
}

const PAGE_SIZES = computed(() => [25, 50, 100, 200, 500].filter((n) => n <= props.maxPageSize))
</script>

<template>
  <div class="flex flex-col gap-4">
    <SegmentedControl
      v-model="draft.grouped"
      class="w-fit"
      :options="[
        { value: false, label: 'Rows' },
        { value: true, label: 'Groups and counts' },
      ]"
      label="Return"
    />

    <!-- Rows: which columns, in the order chosen. -->
    <fieldset v-if="!draft.grouped" class="flex flex-col gap-1.5">
      <legend :class="miniLabel" class="mb-1">Columns, in the order you choose them</legend>
      <div class="flex flex-wrap gap-1.5">
        <button
          v-for="f in fields"
          :key="f.name"
          type="button"
          :aria-pressed="draft.columns.includes(f.name)"
          :aria-label="allowed(f) ? f.label : `${f.label}, needs ${f.permission}`"
          :disabled="!allowed(f)"
          :title="allowed(f) ? f.description : `Needs ${f.permission}`"
          class="rounded-full border px-2.5 py-0.5 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
          :class="
            draft.columns.includes(f.name)
              ? 'border-[var(--c-accent)] bg-[var(--c-accent-subtle)] text-[var(--c-accent)]'
              : 'bg-[var(--c-surface)] text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
          "
          @click="toggle(draft.columns, f.name)"
        >
          <span v-if="draft.columns.includes(f.name)" class="tabular mr-1 font-semibold">{{ draft.columns.indexOf(f.name) + 1 }}</span>
          {{ f.label }}
        </button>
      </div>
      <p v-if="draft.columns.length === 0" class="text-2xs text-[var(--c-text-muted)]">
        None chosen: the dataset's default columns, of those you may see.
      </p>
    </fieldset>

    <template v-else>
      <fieldset class="flex flex-col gap-1.5">
        <legend :class="miniLabel" class="mb-1">Group by</legend>
        <div class="flex flex-wrap gap-1.5">
          <button
            v-for="f in groupable"
            :key="f.name"
            type="button"
            :aria-pressed="draft.groupBy.includes(f.name)"
            :aria-label="allowed(f) ? f.label : `${f.label}, needs ${f.permission}`"
            :disabled="!allowed(f)"
            :title="allowed(f) ? f.description : `Needs ${f.permission}`"
            class="rounded-full border px-2.5 py-0.5 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
            :class="
              draft.groupBy.includes(f.name)
                ? 'border-[var(--c-accent)] bg-[var(--c-accent-subtle)] text-[var(--c-accent)]'
                : 'bg-[var(--c-surface)] text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]'
            "
            @click="toggle(draft.groupBy, f.name)"
          >
            {{ f.label }}
          </button>
        </div>
        <p class="text-2xs text-[var(--c-text-muted)]">
          No grouping: one row of measures over everything that matches.
        </p>
      </fieldset>

      <fieldset class="flex flex-col gap-1.5">
        <legend :class="miniLabel" class="mb-1">Measures</legend>
        <div v-for="m in draft.measures" :key="m.id" class="flex flex-wrap items-center gap-1.5">
          <select
            :value="m.aggregate"
            :class="control"
            aria-label="Measure"
            @change="change(m, { aggregate: ($event.target as HTMLSelectElement).value as ExplorerAggregate })"
          >
            <option v-for="a in AGGREGATES" :key="a.value" :value="a.value">{{ a.label }}</option>
          </select>

          <select
            v-if="m.aggregate !== 'Count'"
            :value="m.field ?? ''"
            :class="control"
            aria-label="Of field"
            @change="change(m, { field: ($event.target as HTMLSelectElement).value })"
          >
            <option v-for="f in m.aggregate === 'CountDistinct' ? fields : dates" :key="f.name" :value="f.name">
              {{ f.label }}
            </option>
          </select>

          <label
            v-if="draft.dataset === 'Bindings'"
            class="inline-flex items-center gap-1 text-2xs text-[var(--c-text-secondary)]"
            title="Count only bindings the feed has not yet removed"
          >
            <input
              type="checkbox"
              :checked="m.activeOnly"
              @change="change(m, { activeOnly: ($event.target as HTMLInputElement).checked })"
            />
            active only
          </label>

          <span class="text-2xs text-[var(--c-text-muted)]">as</span>
          <input v-model="m.name" :class="mono" class="w-36" aria-label="Measure name" spellcheck="false" />

          <button
            type="button"
            :class="iconButton"
            aria-label="Remove measure"
            @click="draft.measures.splice(draft.measures.indexOf(m), 1)"
          >
            <span aria-hidden="true" class="text-lg leading-none">&times;</span>
          </button>
        </div>
        <Button size="sm" variant="ghost" class="w-fit" :disabled="draft.measures.length >= 10" @click="addMeasure">
          + Add measure
        </Button>
      </fieldset>

      <fieldset class="flex flex-col gap-1.5">
        <legend :class="miniLabel" class="mb-1">Only groups where</legend>
        <ConditionTree
          v-if="measureFields.length > 0"
          :node="draft.having"
          :fields="measureFields"
          :max-depth="maxDepth"
          :problems="problems"
          :allowed="() => true"
          noun="condition on a measure"
        />
        <p v-else class="text-2xs text-[var(--c-text-muted)]">Add a measure to filter groups by it.</p>
      </fieldset>
    </template>

    <div class="flex flex-wrap items-end gap-4">
      <fieldset class="flex flex-col gap-1.5">
        <legend :class="miniLabel" class="mb-1">Order</legend>
        <div v-for="(s, i) in draft.sort" :key="i" class="flex items-center gap-1.5">
          <select v-model="s.field" :class="control" aria-label="Order by">
            <option v-for="o in sortable" :key="o.name" :value="o.name">{{ o.label }}</option>
          </select>
          <select v-model="s.descending" :class="control" aria-label="Direction">
            <option :value="false">ascending</option>
            <option :value="true">descending</option>
          </select>
          <button type="button" :class="iconButton" aria-label="Remove ordering" @click="draft.sort.splice(i, 1)">
            <span aria-hidden="true" class="text-lg leading-none">&times;</span>
          </button>
        </div>
        <Button
          size="sm"
          variant="ghost"
          class="w-fit"
          :disabled="draft.sort.length >= 3 || sortable.length <= draft.sort.length"
          @click="addSort"
        >
          + Add ordering
        </Button>
      </fieldset>

      <label class="flex flex-col gap-1">
        <span :class="miniLabel">Rows per page</span>
        <select v-model.number="draft.pageSize" :class="control">
          <option v-for="n in PAGE_SIZES" :key="n" :value="n">{{ n }}</option>
        </select>
      </label>
    </div>
  </div>
</template>
