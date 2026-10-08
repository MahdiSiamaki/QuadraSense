<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError, saveFile, type ProblemDetails } from '@/api/client'
import {
  useDeleteSavedQuery,
  useExplorerCatalogue,
  useExplorerExport,
  useExplorerPlan,
  useExplorerRun,
  useSavedQueries,
  useSaveQuery,
  type ExplorerDataset,
  type ExplorerField,
  type ExplorerPlan,
  type ExplorerQueryRequest,
  type SavedQuery,
} from '@/api/explorer'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import SplitView from '@/design-system/SplitView.vue'
import Modal from '@/design-system/Modal.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import { formatDate } from '@/lib/format'
import ConditionTree from './ConditionTree.vue'
import EntityPanel from './EntityPanel.vue'
import {
  daysBefore,
  emptyDraft,
  fromRequest,
  newCondition,
  placeProblems,
  toRequest,
  type PlacedProblems,
  type QueryDraft,
  type SortDraft,
} from './model'
import PlanSummary from './PlanSummary.vue'
import ResultGrid from './ResultGrid.vue'
import SavedQueriesPanel from './SavedQueriesPanel.vue'
import SaveQueryDialog from './SaveQueryDialog.vue'
import ShapeEditor from './ShapeEditor.vue'
import TemplatesPanel from './TemplatesPanel.vue'
import TimelineView from '@/features/timeline/TimelineView.vue'
import type { ExplorerTemplate } from './templates'
import { segment } from './ui'

/**
 * The Explorer: ask questions of current bindings and the dated event log.
 *
 * Nothing here writes SQL, and nothing here decides what may run - the server checks every
 * query against its catalogue, the caller's permissions and a budget, and says why when it
 * refuses. The page's job is to make a good question easy to ask and a refused one easy to fix:
 * validation messages land on the condition they are about, and a query over the budget comes
 * back with the server's own suggestions for narrowing it.
 *
 * Identifiers never enter the URL. Queries are POSTed; drill-down opens a panel, not a route.
 */
const { can } = useAuth()
const catalogue = useExplorerCatalogue()
const run = useExplorerRun()
const planner = useExplorerPlan()
const exporter = useExplorerExport()
const saved = useSavedQueries()
const saveQuery = useSaveQuery()
const deleteQuery = useDeleteSavedQuery()

type Tab = 'build' | 'templates' | 'saved'
const tab = ref<Tab>('build')

const dataThrough = computed(() => catalogue.data.value?.dataThrough ?? null)
const maxDepth = 6

const fieldsByDataset = computed<Record<string, ExplorerField[]>>(() =>
  Object.fromEntries((catalogue.data.value?.datasets ?? []).map((d) => [d.dataset, d.fields])),
)
const dataset = computed(() => catalogue.data.value?.datasets.find((d) => d.dataset === draft.value.dataset))
const fields = computed(() => dataset.value?.fields ?? [])
const allowed = (field: ExplorerField) => !field.permission || can(field.permission)

// ------------------------------------------------------------------ the draft

/**
 * A fresh query for a dataset: its default columns, shown as chosen so nobody has to guess what
 * "defaults" means, and a first condition to fill in. The event log starts on its latest day,
 * because it cannot be read without a date range and one day is 11-13 million rows.
 */
function starter(which: ExplorerDataset): QueryDraft {
  const draft = emptyDraft(which)
  const info = catalogue.data.value?.datasets.find((d) => d.dataset === which)
  const own = info?.fields ?? []
  draft.columns = (info?.defaultColumns ?? []).filter((c) => {
    const f = own.find((x) => x.name === c)
    return f !== undefined && allowed(f)
  })

  if (which === 'Events' && dataThrough.value) {
    const date = own.find((f) => f.name === 'date')
    const condition = newCondition(date, 'Between')
    condition.values = [dataThrough.value, dataThrough.value]
    draft.where.children.push(condition)
  }

  draft.where.children.push(newCondition(own.find(allowed)))
  return draft
}

const draft = ref<QueryDraft>(emptyDraft())
const ready = ref(false)
const editing = ref<{ id: number; name: string; description: string } | null>(null)
const template = ref<ExplorerTemplate | null>(null)

// The catalogue arrives after the first render, and the starter needs it. Once only: a refetch
// must not throw away a query somebody is half-way through building.
watch(
  () => catalogue.data.value,
  (data) => {
    if (!data || ready.value) return
    draft.value = starter('Bindings')
    ready.value = true
  },
  { immediate: true },
)

const tabs = computed<Array<{ id: Tab; label: string }>>(() => [
  { id: 'build', label: 'Build' },
  { id: 'templates', label: 'Templates' },
  { id: 'saved', label: `My queries${saved.data.value ? ` (${saved.data.value.length})` : ''}` },
])

function chooseDataset(which: ExplorerDataset) {
  if (which === draft.value.dataset) return
  draft.value = starter(which)
  editing.value = null
  template.value = null
  clearFeedback()
}

function newQuery() {
  draft.value = starter(draft.value.dataset)
  editing.value = null
  template.value = null
  clearFeedback()
}

// ------------------------------------------------------------------ running

const problems = ref<PlacedProblems>({ byNode: new Map(), general: [] })
const message = ref<{ tone: 'danger' | 'warning'; text: string } | null>(null)
const shownPlan = ref<ExplorerPlan | null>(null)

/** What the grid shows was produced by this request; paging, sorting and export reuse it. */
const ranRequest = ref<ExplorerQueryRequest | null>(null)
/**
 * The last result that came back, kept while the next page, sort or query runs.
 *
 * Read straight from the mutation it was undefined from the moment a request started, so the grid
 * unmounted on every page click: the column beside the entity panel went empty, the page jumped,
 * and the grid's own dimmed loading state could never be seen.
 */
const shownResult = ref<typeof run.data.value | null>(null)
const result = computed(() => shownResult.value ?? null)
const ranSort = computed<SortDraft[]>(() =>
  (ranRequest.value?.sort ?? []).map((s) => ({ field: s.field, descending: s.descending ?? false })),
)
const resultGrouped = computed(
  () => (ranRequest.value?.groupBy?.length ?? 0) > 0 || (ranRequest.value?.measures?.length ?? 0) > 0,
)

function clearFeedback() {
  problems.value = { byNode: new Map(), general: [] }
  message.value = null
}

/** Turns a refusal into something next to what caused it. */
function explain(error: unknown, paths: Map<string, string>) {
  if (!(error instanceof ApiError)) {
    message.value = { tone: 'danger', text: 'Could not reach the server.' }
    return
  }

  // A refusal over the budget carries the plan, as a problem-details extension.
  const problem = error.problem as (ProblemDetails & { plan?: ExplorerPlan }) | null

  switch (error.status) {
    case 400:
      problems.value = placeProblems(problem?.errors ?? {}, paths)
      if (problems.value.byNode.size + problems.value.general.length === 0) {
        message.value = { tone: 'danger', text: problem?.title ?? 'The query cannot run as written.' }
      }
      return
    case 422:
      if (problem?.plan) shownPlan.value = problem.plan
      message.value = { tone: 'warning', text: problem?.detail ?? 'Over the query budget.' }
      return
    case 429:
      message.value = { tone: 'warning', text: problem?.detail ?? 'Busy - try again in a moment.' }
      return
    default:
      message.value = { tone: 'danger', text: problem?.detail ?? problem?.title ?? `Server responded ${error.status}.` }
  }
}

function execute(request: ExplorerQueryRequest, paths = new Map<string, string>()) {
  clearFeedback()
  run.mutate(request, {
    onSuccess: (data) => {
      ranRequest.value = request
      shownPlan.value = data.plan
      shownResult.value = data
    },
    onError: (error) => {
      // A refused run leaves nothing to show: keeping the previous rows would present them as the
      // answer to the query that was just refused.
      shownResult.value = null
      explain(error, paths)
    },
  })
}

function runDraft() {
  const { request, paths } = toRequest(draft.value, 1)
  execute(request, paths)
}

function checkCost() {
  const { request, paths } = toRequest(draft.value, 1)
  clearFeedback()
  planner.mutate(request, {
    onSuccess: (plan) => (shownPlan.value = plan),
    onError: (error) => explain(error, paths),
  })
}

// While a run is pending the grid on screen is the previous one: paging or sorting it would start
// a run of the OLD query, and only the latest run's result is ever shown - the pending one's would
// be lost though it ran and was audited.
function goToPage(page: number) {
  if (ranRequest.value && !run.isPending.value) execute({ ...ranRequest.value, page })
}

/** A header click: re-runs what is on screen in the new order, and the builder follows. */
function resort(sort: SortDraft[]) {
  if (!ranRequest.value || run.isPending.value) return
  draft.value.sort = sort.map((s) => ({ ...s }))
  execute({ ...ranRequest.value, sort, page: 1 })
}

function exportCsv() {
  const request = ranRequest.value
  if (!request) return
  exporter.mutate(request, {
    onSuccess: (file) =>
      saveFile(file, `explorer-${request.dataset.toLowerCase()}-${new Date().toISOString().slice(0, 10)}.csv`),
    onError: (error) => explain(error, new Map()),
  })
}

/** Puts a query into the builder - from a template, a saved query or the summary panel - and runs it. */
function load(request: ExplorerQueryRequest, andRun: boolean) {
  draft.value = fromRequest(request)
  tab.value = 'build'
  clearFeedback()
  if (andRun) runDraft()
}

function useTemplate(request: ExplorerQueryRequest, t: ExplorerTemplate) {
  editing.value = null
  template.value = t
  load(request, true)
}

// ------------------------------------------------------------------ drill-down

/** Identifiers opened, most recent last. Page state only - never the URL. */
const stack = ref<string[]>([])
const current = computed(() => stack.value[stack.value.length - 1] ?? null)

function drill(identifier: string) {
  if (current.value !== identifier) stack.value.push(identifier)
}

/**
 * The timeline, when open, is of whatever the summary panel shows: following a SIM out of a
 * handset's timeline moves both to the SIM, and Back moves both back. Closed with the panel.
 */
const timelineOpen = ref(false)
watch(current, (identifier) => {
  if (identifier === null) timelineOpen.value = false
})

function closePanel() {
  stack.value = []
}

function explore(request: ExplorerQueryRequest) {
  editing.value = null
  template.value = null
  load(request, true)
}

// ------------------------------------------------------------------ My Queries

const dialog = ref<{ mode: 'save' | 'rename'; target: SavedQuery | null; suggestion: string } | null>(null)
const confirmDelete = ref<SavedQuery | null>(null)

const dialogEditing = computed(() => {
  const d = dialog.value
  if (!d) return null
  if (d.mode === 'rename' && d.target) return { id: d.target.id, name: d.target.name, description: d.target.description }
  return editing.value
})

function openSave() {
  saveQuery.reset()
  dialog.value = { mode: 'save', target: null, suggestion: template.value?.title ?? '' }
}

function save(value: { name: string; description: string; asNew: boolean }) {
  const d = dialog.value
  if (!d) return

  const renaming = d.mode === 'rename' && d.target
  const id = value.asNew ? undefined : renaming ? d.target!.id : editing.value?.id
  const query = renaming ? d.target!.query : toRequest(draft.value, 1).request

  saveQuery.mutate(
    { id, name: value.name, description: value.description, query },
    {
      onSuccess: (s) => {
        if (!renaming || editing.value?.id === s.id) {
          editing.value = { id: s.id, name: s.name, description: s.description }
        }
        dialog.value = null
      },
    },
  )
}

function runSaved(q: SavedQuery) {
  editing.value = { id: q.id, name: q.name, description: q.description }
  template.value = null
  load(q.query, true)
}

function editSaved(q: SavedQuery) {
  editing.value = { id: q.id, name: q.name, description: q.description }
  template.value = null
  load(q.query, false)
}

function duplicateSaved(q: SavedQuery) {
  editing.value = null
  template.value = null
  load(q.query, false)
  saveQuery.reset()
  dialog.value = { mode: 'save', target: null, suggestion: `${q.name} (copy)`.slice(0, 100) }
}

function renameSaved(q: SavedQuery) {
  saveQuery.reset()
  dialog.value = { mode: 'rename', target: q, suggestion: q.name }
}

function removeSaved() {
  const q = confirmDelete.value
  if (!q) return
  deleteQuery.mutate(q.id, {
    onSuccess: () => {
      if (editing.value?.id === q.id) editing.value = null
      confirmDelete.value = null
    },
  })
}

const eventsNote = computed(() =>
  draft.value.dataset === 'Events' && dataThrough.value
    ? `A date range is required, at the top level. One day of the log is 11-13 million rows; a number, SIM or handset narrows it to a few thousand. Last 90 days: ${daysBefore(dataThrough.value, 89)} to ${dataThrough.value}.`
    : null,
)
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Explorer</h1>
        <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          Ask questions of current bindings and the dated event log. Every query is costed by the server before it
          runs, kept within a budget, and recorded in the audit log - which fields, never which values.
        </p>
      </div>

      <p
        v-if="dataThrough"
        class="tabular inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs text-[var(--c-text-secondary)]"
        title="The latest day in the event log. Every answer here is as of this day."
      >
        <span class="size-1.5 rounded-full bg-[var(--c-success)]" aria-hidden="true" />
        Data through {{ formatDate(dataThrough) }}
      </p>
    </header>

    <div class="inline-flex w-fit overflow-hidden rounded-[var(--radius-md)] border" role="tablist" aria-label="Explorer">
      <button
        v-for="t in tabs"
        :key="t.id"
        type="button"
        role="tab"
        :aria-selected="tab === t.id"
        :class="segment(tab === t.id)"
        @click="tab = t.id"
      >
        {{ t.label }}
      </button>
    </div>

    <AsyncBoundary
      :is-loading="catalogue.isPending.value"
      :is-error="catalogue.isError.value"
      :error="catalogue.error.value"
      min-height="12rem"
      @retry="catalogue.refetch()"
    >
      <!-- Build -->
      <Card v-if="ready && tab === 'build'">
        <form class="flex flex-col gap-5" @submit.prevent="runDraft">
          <div class="flex flex-wrap items-center gap-3">
            <div class="inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" aria-label="Dataset">
              <button
                v-for="d in catalogue.data.value?.datasets ?? []"
                :key="d.dataset"
                type="button"
                role="radio"
                :aria-checked="draft.dataset === d.dataset"
                :aria-label="d.label"
                :class="segment(draft.dataset === d.dataset)"
                :title="d.description"
                @click="chooseDataset(d.dataset)"
              >
                {{ d.label }}
              </button>
            </div>
            <p class="min-w-0 flex-1 text-xs text-[var(--c-text-muted)]">{{ dataset?.description }}</p>

            <span
              v-if="editing"
              class="inline-flex items-center gap-1 rounded-full bg-[var(--c-accent-subtle)] px-2 py-0.5 text-2xs font-medium text-[var(--c-accent)]"
            >
              Saved query: {{ editing.name }}
            </span>
            <Button size="sm" variant="ghost" @click="newQuery">New query</Button>
          </div>

          <section class="flex flex-col gap-2" aria-labelledby="explorer-where">
            <h2 id="explorer-where" class="text-sm font-semibold">Which rows</h2>
            <p v-if="eventsNote" class="text-2xs text-[var(--c-text-muted)]">{{ eventsNote }}</p>
            <ConditionTree
              :node="draft.where"
              :fields="fields"
              :max-depth="maxDepth"
              :problems="problems.byNode"
              :allowed="allowed"
            />
          </section>

          <section class="flex flex-col gap-2 border-t pt-4" aria-labelledby="explorer-shape">
            <h2 id="explorer-shape" class="text-sm font-semibold">What to return</h2>
            <ShapeEditor
              :draft="draft"
              :fields="fields"
              :allowed="allowed"
              :max-depth="maxDepth"
              :max-page-size="catalogue.data.value?.maxPageSize ?? 500"
              :problems="problems.byNode"
            />
          </section>

          <ul
            v-if="problems.general.length"
            class="flex flex-col gap-0.5 rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] px-3 py-2 text-xs text-[var(--c-danger-text)]"
            role="alert"
          >
            <li v-for="(p, i) in problems.general" :key="i">{{ p }}</li>
          </ul>

          <div class="flex flex-wrap items-center gap-2 border-t pt-4">
            <Button type="submit" variant="primary" :pending="run.isPending.value">Run</Button>
            <Button :pending="planner.isPending.value" @click="checkCost">Check cost</Button>
            <Button variant="ghost" @click="openSave">{{ editing ? 'Save…' : 'Save to My Queries…' }}</Button>
          </div>

          <p
            v-if="message"
            class="rounded-[var(--radius-md)] border px-3 py-2 text-xs"
            :style="{
              borderColor: `var(--c-${message.tone})`,
              backgroundColor: `var(--c-${message.tone}-subtle)`,
              color: `var(--c-${message.tone})`,
            }"
            role="alert"
          >
            {{ message.text }}
          </p>

          <PlanSummary v-if="shownPlan" :plan="shownPlan" />
        </form>
      </Card>

      <!-- Templates -->
      <Card
        v-if="ready && tab === 'templates'"
        title="Templates"
        subtitle="Common questions, ready to fill in. Each one opens in the builder, where you can see and change what it asks."
      >
        <TemplatesPanel :fields-by-dataset="fieldsByDataset" :data-through="dataThrough" :can="can" @use="useTemplate" />
      </Card>

      <!-- My Queries -->
      <Card
        v-if="ready && tab === 'saved'"
        title="My queries"
        subtitle="Saved definitions, private to you. Each runs again, on today's data, when you open it."
        flush
      >
        <SavedQueriesPanel
          :saved="saved.data.value"
          :is-loading="saved.isPending.value"
          :is-error="saved.isError.value"
          :error="saved.error.value"
          :current-id="editing?.id ?? null"
          @run="runSaved"
          @edit="editSaved"
          @duplicate="duplicateSaved"
          @rename="renameSaved"
          @remove="(q) => (confirmDelete = q)"
          @retry="saved.refetch()"
        />
      </Card>
    </AsyncBoundary>

    <!-- Results, and the summary panel beside them; the timeline under the results. -->
    <SplitView v-if="result || current" :aside="!!current">
      <p
        v-if="template?.caution && result"
        class="rounded-[var(--radius-md)] border px-3 py-2 text-xs text-[var(--c-text-secondary)]"
      >
        <span class="font-semibold">{{ template.title }}.</span> {{ template.caution }}
      </p>

      <Card v-if="result" flush>
        <ResultGrid
          :result="result"
          :grouped="resultGrouped"
          :sort="ranSort"
          :loading="run.isPending.value"
          :can-export="can(Permission.DataExport)"
          :exporting="exporter.isPending.value"
          @sort="resort"
          @page="goToPage"
          @drill="drill"
          @export="exportCsv"
        />
      </Card>

      <TimelineView
        v-if="timelineOpen && current"
        :identifier="current"
        @drill="drill"
        @close="timelineOpen = false"
      />

      <template #aside>
        <EntityPanel
          v-if="current"
          :key="current"
          :identifier="current"
          :depth="stack.length"
          :data-through="dataThrough"
          :can-open-device="can(Permission.DeviceView)"
          @back="stack.pop()"
          @close="closePanel"
          @explore="explore"
          @timeline="timelineOpen = true"
        />
      </template>
    </SplitView>

    <SaveQueryDialog
      :open="dialog !== null"
      :editing="dialogEditing"
      :suggestion="dialog?.suggestion ?? ''"
      :busy="saveQuery.isPending.value"
      :error="saveQuery.error.value"
      @close="dialog = null"
      @save="save"
    />

    <Modal
      :open="confirmDelete !== null"
      title="Delete saved query"
      :description="confirmDelete ? `“${confirmDelete.name}” will be removed from My Queries. This cannot be undone.` : ''"
      size="sm"
      :busy="deleteQuery.isPending.value"
      @close="confirmDelete = null"
    >
      <p v-if="deleteQuery.isError.value" class="text-xs text-[var(--c-danger-text)]" role="alert">
        Could not delete it. Try again.
      </p>
      <p v-else class="text-xs text-[var(--c-text-secondary)]">
        Only the definition is deleted; no data is.
      </p>
      <template #actions>
        <Button variant="ghost" :disabled="deleteQuery.isPending.value" @click="confirmDelete = null">Cancel</Button>
        <Button variant="danger" :pending="deleteQuery.isPending.value" @click="removeSaved">Delete</Button>
      </template>
    </Modal>
  </div>
</template>
