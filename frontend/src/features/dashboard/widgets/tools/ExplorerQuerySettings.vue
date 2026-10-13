<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useExplorerCatalogue, type ExplorerDataset, type ExplorerField } from '@/api/explorer'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import ConditionTree from '@/features/explorer/ConditionTree.vue'
import { emptyDraft, fromRequest, nextId, suggestMeasureName, toRequest, type QueryDraft } from '@/features/explorer/model'
import ShapeEditor from '@/features/explorer/ShapeEditor.vue'
import { miniLabel } from '@/features/explorer/ui'
import type { WidgetSettingsProps } from '@/features/dashboard/types/widget'
import type { ExplorerAggregateConfig } from './config'

/**
 * The Explorer aggregate's query, built with the Explorer's own condition tree and shape editor.
 *
 * Two things differ from the Explorer page, both because a dashboard is shared: the query is always
 * groups and counts, and no field that names a number, SIM or handset is offered at all - not even to
 * someone who may look them up - because a stored definition never holds an identifier. The server
 * refuses either on save (ADR-016 §10); this form simply never builds one.
 */
const props = defineProps<WidgetSettingsProps<ExplorerAggregateConfig>>()
const emit = defineEmits<{ 'update:config': [config: ExplorerAggregateConfig] }>()

const catalogue = useExplorerCatalogue()
const MAX_DEPTH = 4

/** A shared definition never names an identifier: fields behind a lookup permission are never offered. */
const allowed = (field: ExplorerField) => !field.permission

function fieldsOf(dataset: ExplorerDataset): ExplorerField[] {
  return catalogue.data.value?.datasets.find((d) => d.dataset === dataset)?.fields ?? []
}

/** A fresh query: groups and one count. */
function start(dataset: ExplorerDataset): QueryDraft {
  const draft = emptyDraft(dataset)
  draft.grouped = true
  const count = { id: nextId(), name: '', aggregate: 'Count' as const, field: null, activeOnly: false }
  count.name = suggestMeasureName(count, fieldsOf(dataset).map((f) => f.name))
  draft.measures.push(count)
  return draft
}

const draft = ref<QueryDraft>(props.config.query ? { ...fromRequest(props.config.query), grouped: true } : start('Bindings'))
const fields = computed(() => fieldsOf(draft.value.dataset))
const noProblems = new Map<string, string[]>()

function emitQuery() {
  emit('update:config', { ...props.config, query: toRequest({ ...draft.value, grouped: true }).request })
}

onMounted(() => {
  if (props.config.query === null) emitQuery()
})
watch(draft, emitQuery, { deep: true })

function switchDataset(dataset: ExplorerDataset) {
  if (dataset === draft.value.dataset) return
  draft.value = start(dataset)
}
</script>

<template>
  <AsyncBoundary
    :is-loading="catalogue.isPending.value"
    :is-error="catalogue.isError.value"
    :error="catalogue.error.value"
    min-height="6rem"
    gap="sm"
    @retry="catalogue.refetch()"
  >
    <div class="flex flex-col gap-4">
      <div>
        <span :class="miniLabel">Data</span>
        <SegmentedControl
          :model-value="draft.dataset"
          class="mt-1"
          :options="(catalogue.data.value?.datasets ?? []).map((d) => ({ value: d.dataset, label: d.label }))"
          label="Dataset"
          @update:model-value="switchDataset"
        />
        <p v-if="draft.dataset === 'Events'" class="mt-1.5 text-2xs text-[var(--c-text-muted)]">
          The event log needs a date range. Set “Last days of data” below - stored as a number of days and counted back
          from the log’s last day each time it runs - or add a date condition here.
        </p>
      </div>

      <section class="flex flex-col gap-2" aria-label="Which rows">
        <span :class="miniLabel">Which rows</span>
        <ConditionTree :node="draft.where" :fields="fields" :max-depth="MAX_DEPTH" :problems="noProblems" :allowed="allowed" />
      </section>

      <section class="flex flex-col gap-2 border-t pt-3" aria-label="Groups and counts">
        <span :class="miniLabel">Groups and counts</span>
        <ShapeEditor
          :draft="draft"
          :fields="fields"
          :allowed="allowed"
          :max-depth="MAX_DEPTH"
          :max-page-size="catalogue.data.value?.maxPageSize ?? 50"
          :problems="noProblems"
          groups-only
        />
      </section>

      <p class="text-2xs text-[var(--c-text-muted)]">
        Numbers, SIMs and handsets can be counted but not filtered, grouped or shown: a dashboard is shared, and its
        definition never holds an identifier. The server checks the query again when the dashboard is saved.
      </p>
    </div>
  </AsyncBoundary>
</template>
