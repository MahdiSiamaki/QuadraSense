<script setup lang="ts">
import { computed, watch } from 'vue'
import { ApiError, saveFile } from '@/api/client'
import { useExplorerCatalogue, useExplorerExport, useExplorerRun, type ExplorerFilter, type ExplorerQueryRequest, type ExplorerValue } from '@/api/explorer'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import Button from '@/design-system/Button.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import { daysBefore } from '@/features/explorer/model'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { ExplorerAggregateConfig } from './config'

/**
 * A saved aggregate Explorer question - counts by group - run on request.
 *
 * Runs only when a person presses Run: each run is the Explorer's own audited, budgeted request, some
 * cost seconds of server time, and none may re-run by itself on focus, Refresh or in an export. The
 * query names no identifier (the server refused it on save otherwise), so anyone the dashboard is
 * shared with and who holds explorer.query can run it as themselves.
 *
 * "Last N days" is stored as N and counted back from the event log's last day when it runs, the way
 * every relative range on a dashboard counts back from the data, never from today.
 */
const props = defineProps<WidgetViewProps<ExplorerAggregateConfig>>()

const { can } = useAuth()
const config = computed(() => props.instance.config)
const catalogue = useExplorerCatalogue()
const dataThrough = computed(() => catalogue.data.value?.dataThrough ?? null)
const run = useExplorerRun()
const exporter = useExplorerExport()
const result = computed(() => run.data.value ?? null)

const window = computed(() => {
  const q = config.value.query
  const n = config.value.lastDays
  if (!q || q.dataset !== 'Events' || !n || !dataThrough.value) return null
  return { from: daysBefore(dataThrough.value, n - 1), to: dataThrough.value }
})

function request(): ExplorerQueryRequest | null {
  const q = config.value.query
  if (!q) return null
  let where = q.where ?? null
  if (window.value) {
    const range: ExplorerFilter = { field: 'date', operator: 'Between', values: [window.value.from, window.value.to] }
    where = where ? { logic: 'And', children: [range, where] } : range
  }
  return { ...q, where, page: 1, pageSize: config.value.rows }
}

const waitingForDates = computed(() => config.value.query?.dataset === 'Events' && !!config.value.lastDays && !dataThrough.value)
const canRun = computed(() => config.value.query !== null && props.mode !== 'export' && !waitingForDates.value)

function go() {
  const r = request()
  if (r && canRun.value) run.mutate(r)
}

async function exportCsv() {
  const r = request()
  if (!r) return
  const file = await exporter.mutateAsync(r)
  saveFile(file, 'explorer-aggregate.csv')
}

// A result is true only of the query it ran: a change clears it.
watch(() => JSON.stringify(config.value), () => run.reset())

const error = computed(() => {
  const e = run.error.value ?? exporter.error.value
  if (!e) return null
  if (e instanceof ApiError) return e.fieldErrors[0]?.message ?? e.problem?.detail ?? e.problem?.title ?? e.message
  return 'The query could not be run.'
})

const measureNames = computed(() => new Set((config.value.query?.measures ?? []).map((m) => m.name)))

function cell(value: ExplorerValue, type: string): string {
  if (value === null) return '—'
  if (typeof value === 'number') return formatFull(value)
  if (type === 'Date' && typeof value === 'string') return formatDate(value)
  return String(value)
}

/** A bar chart needs one group column and a numeric measure; otherwise the table is drawn. */
const bars = computed(() => {
  const r = result.value
  if (!r || config.value.display !== 'bar') return null
  const groups = r.columns.map((c, i) => ({ c, i })).filter(({ c }) => !measureNames.value.has(c.name))
  const measure = r.columns.map((c, i) => ({ c, i })).find(({ c }) => measureNames.value.has(c.name) && c.type === 'Number')
  if (groups.length !== 1 || !measure) return null
  const g = groups[0]!
  const rows = r.rows.map((row) => ({ key: cell(row[g.i] ?? null, g.c.type), count: Number(row[measure.i] ?? 0) }))
  const total = rows.reduce((s, x) => s + x.count, 0) || 1
  return { unit: measure.c.label, rows: rows.map((x) => ({ ...x, percent: (100 * x.count) / total })) }
})

const scope = computed(() => {
  const q = config.value.query
  if (!q) return ''
  const what = q.dataset === 'Events' ? 'The event log' : 'Current bindings'
  return window.value ? `${what}, ${formatDate(window.value.from)} – ${formatDate(window.value.to)}` : what
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <div class="flex flex-wrap items-start justify-between gap-2">
      <p class="min-w-0 text-xs text-[var(--c-text-secondary)]">
        <template v-if="config.query">{{ scope }}.</template>
        <template v-else>No query yet: build one in the widget’s settings.</template>
      </p>
      <div class="flex shrink-0 gap-2">
        <Button
          v-if="result && can(Permission.DataExport)"
          type="button"
          size="sm"
          variant="ghost"
          :disabled="exporter.isPending.value || result.total === 0"
          @click="exportCsv"
        >
          {{ exporter.isPending.value ? 'Exporting…' : 'Export CSV' }}
        </Button>
        <Button type="button" size="sm" :disabled="!canRun || run.isPending.value" @click="go">
          {{ run.isPending.value ? 'Running…' : result ? 'Run again' : 'Run' }}
        </Button>
      </div>
    </div>

    <p v-if="error" class="rounded-[var(--radius-md)] border px-3 py-2 text-xs text-[var(--c-text-secondary)]" role="alert">{{ error }}</p>
    <p v-else-if="!result && !run.isPending.value && config.query" class="py-6 text-center text-xs text-[var(--c-text-muted)]">
      Runs only when you press Run: each run is audited and counts against the Explorer’s budget.
    </p>

    <AsyncBoundary v-if="result || run.isPending.value" :class="bars ? 'shrink-0' : 'min-h-0 flex-1'" :is-loading="run.isPending.value && !result" :is-error="false" min-height="6rem" gap="none">
      <div v-if="bars" class="relative">
        <BarChart :data="bars.rows" :unit="bars.unit" height="auto" :selectable="false" />
      </div>
      <div v-else-if="result" class="min-h-0 flex-1 overflow-auto overscroll-contain rounded-[var(--radius-md)] border">
        <table class="w-full text-xs" :aria-busy="run.isPending.value">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th v-for="c in result.columns" :key="c.name" scope="col" class="px-2 py-1.5 font-medium" :class="measureNames.has(c.name) ? 'text-right' : ''">
                {{ c.label }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="result.rows.length === 0">
              <td :colspan="result.columns.length" class="px-2 py-6 text-center text-[var(--c-text-muted)]">Nothing matches.</td>
            </tr>
            <tr v-for="(row, i) in result.rows" :key="i" class="border-t">
              <td v-for="(c, j) in result.columns" :key="c.name" class="px-2 py-1.5" :class="measureNames.has(c.name) ? 'tabular text-right' : ''">
                {{ cell(row[j] ?? null, c.type) }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>

    <p v-if="result" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      {{ formatFull(result.rows.length) }} of {{ formatFull(result.total) }} {{ result.total === 1 ? 'group' : 'groups' }}<template v-if="config.display === 'bar' && !bars">
        - drawn as a table: a bar chart needs exactly one group column and a count</template>.
      Counts are exact. {{ formatFull(result.rowsRead) }} rows read in {{ formatFull(result.elapsedMs) }} ms ({{ result.plan.verdict.toLowerCase() }}).
      <template v-if="dataThrough">Data through {{ formatDate(dataThrough) }}.</template>
    </p>
  </div>
</template>
