<script setup lang="ts">
import { computed } from 'vue'
import type { ExplorerColumn, ExplorerResult, ExplorerValue } from '@/api/explorer'
import Button from '@/design-system/Button.vue'
import Pagination from '@/design-system/Pagination.vue'
import { formatDate, formatFull, formatImei, formatMsisdn } from '@/lib/format'
import type { SortDraft } from './model'

/**
 * A page of Explorer results.
 *
 * Numbers, SIMs, handsets and TACs are buttons: choosing one opens its summary beside the
 * grid, in the page - never in the address bar, where an identifier would reach history and
 * logs. A masked value is plain text, because a redacted identifier cannot be looked up.
 *
 * Headers sort on the server, not in the browser: this page is a slice of up to 10,000 rows,
 * and sorting a slice reorders the wrong rows.
 */
const props = defineProps<{
  result: ExplorerResult
  /** Groups and measures rather than rows: decides the word under the count. */
  grouped: boolean
  sort: SortDraft[]
  loading: boolean
  canExport: boolean
  exporting: boolean
}>()

const emit = defineEmits<{
  sort: [sort: SortDraft[]]
  page: [page: number]
  drill: [identifier: string]
  export: []
}>()

const DRILL_LENGTH: Record<string, number> = { Msisdn: 10, Imsi: 15, Imei: 14, Tac: 8 }

function drillable(value: ExplorerValue, column: ExplorerColumn): value is string {
  const length = DRILL_LENGTH[column.type]
  return !column.masked && length !== undefined && typeof value === 'string' && value.length === length && /^\d+$/.test(value)
}

function display(value: ExplorerValue, column: ExplorerColumn): string {
  if (value === null) return '—'
  switch (column.type) {
    case 'Msisdn':
      return column.masked ? String(value) : formatMsisdn(String(value))
    case 'Imei':
      return column.masked ? String(value) : formatImei(String(value))
    case 'Date':
      return formatDate(String(value))
    case 'Boolean':
      return column.name === 'active' ? (value ? 'Active' : 'Ended') : value ? 'Yes' : 'No'
    case 'Number':
      return typeof value === 'number' ? formatFull(value) : String(value)
    default:
      return String(value)
  }
}

/** Why a cell is empty, where the reason is known and matters. */
function emptyReason(column: ExplorerColumn): string | null {
  if (column.name === 'lastChangeDate') return 'dump only'
  if (['brand', 'model', 'manufacturer', 'deviceType', 'operatingSystem'].includes(column.name)) return 'not in GSMA'
  return null
}

const numeric = (column: ExplorerColumn) => column.type === 'Number'
const digits = (column: ExplorerColumn) => ['Msisdn', 'Imsi', 'Imei', 'Tac'].includes(column.type)

const masked = computed(() => props.result.columns.some((c) => c.masked))
const truncated = computed(() => props.result.total > props.result.reachable)

function sortState(name: string): 'ascending' | 'descending' | 'none' {
  const first = props.sort[0]
  if (first?.field !== name) return 'none'
  return first.descending ? 'descending' : 'ascending'
}

/** First click: largest or latest first for counts and dates, A-Z otherwise. Second click: reverse. */
function toggleSort(column: ExplorerColumn) {
  const state = sortState(column.name)
  const descending = state === 'none' ? column.type === 'Number' || column.type === 'Date' : state === 'ascending'
  emit('sort', [{ field: column.name, descending }])
}
</script>

<template>
  <div class="flex flex-col">
    <div class="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-2.5">
      <p class="tabular text-xs text-[var(--c-text-secondary)]" role="status">
        <span class="font-semibold text-[var(--c-text)]">{{ formatFull(result.total) }}</span>
        {{ grouped ? (result.total === 1 ? 'group' : 'groups') : result.total === 1 ? 'row' : 'rows' }}
        <span class="text-[var(--c-text-muted)]">
          · {{ formatFull(result.elapsedMs) }} ms · {{ formatFull(result.rowsRead) }} rows read
        </span>
      </p>

      <Button
        size="sm"
        :disabled="!canExport || result.total === 0"
        :pending="exporting"
        :title="canExport ? `Up to the first ${formatFull(result.reachable)} rows, as CSV` : 'Exporting needs the data.export permission'"
        @click="emit('export')"
      >
        Export CSV
      </Button>
    </div>

    <p
      v-if="truncated"
      class="border-b bg-[var(--c-warning-subtle)] px-4 py-1.5 text-2xs text-[var(--c-warning-text)]"
    >
      Paging reaches the first {{ formatFull(result.reachable) }} of {{ formatFull(result.total) }}. Narrow the
      query to see the rest - a shorter date range, a model, a number prefix.
    </p>

    <p
      v-if="masked"
      class="border-b bg-[var(--c-warning-subtle)] px-4 py-1.5 text-2xs text-[var(--c-warning-text)]"
    >
      Identifiers are masked: your account does not hold <code class="font-mono">identifier.reveal</code>, so the
      server redacted them before sending. A masked value cannot be opened.
    </p>

    <div v-if="result.rows.length === 0" class="px-4 py-10 text-center text-sm text-[var(--c-text-secondary)]">
      Nothing matches.
    </div>

    <div v-else class="overflow-x-auto transition-opacity" :class="loading ? 'opacity-60' : ''">
      <table class="w-full text-left text-sm">
        <thead class="border-b text-2xs tracking-wide text-[var(--c-text-muted)] uppercase">
          <tr>
            <th
              v-for="column in result.columns"
              :key="column.name"
              scope="col"
              class="px-4 py-2 font-medium whitespace-nowrap"
              :class="numeric(column) ? 'text-right' : ''"
              :aria-sort="sortState(column.name)"
            >
              <button
                type="button"
                class="inline-flex items-center gap-1 uppercase hover:text-[var(--c-text)]"
                :title="`Sort by ${column.label}`"
                :aria-label="`Sort by ${column.label}`"
                @click="toggleSort(column)"
              >
                {{ column.label }}
                <span aria-hidden="true" class="w-2">
                  {{ sortState(column.name) === 'ascending' ? '↑' : sortState(column.name) === 'descending' ? '↓' : '' }}
                </span>
              </button>
            </th>
          </tr>
        </thead>

        <tbody class="divide-y">
          <tr v-for="(row, r) in result.rows" :key="r" class="hover:bg-[var(--c-surface-hover)]">
            <td
              v-for="(column, c) in result.columns"
              :key="column.name"
              class="px-4 py-1.5 whitespace-nowrap"
              :class="[
                numeric(column) ? 'tabular text-right' : '',
                digits(column) ? 'tabular font-mono text-xs' : '',
              ]"
            >
              <button
                v-if="drillable(row[c] ?? null, column)"
                type="button"
                class="hover:text-[var(--c-accent)] hover:underline focus-visible:outline-2 focus-visible:outline-[var(--c-accent)]"
                :title="`Open this ${column.label.toLowerCase()}`"
                :aria-label="`Open ${column.label.toLowerCase()} ${display(row[c] ?? null, column)}`"
                @click="emit('drill', row[c] as string)"
              >
                {{ display(row[c] ?? null, column) }}
              </button>
              <span
                v-else-if="(row[c] ?? null) === null && emptyReason(column)"
                class="text-xs text-[var(--c-text-muted)]"
              >
                {{ emptyReason(column) }}
              </span>
              <span
                v-else-if="column.name === 'active'"
                class="inline-flex items-center gap-1.5 text-xs"
                :style="{ color: row[c] ? 'var(--c-text-secondary)' : 'var(--c-text-muted)' }"
              >
                <span
                  class="size-1.5 rounded-full"
                  :style="{ backgroundColor: row[c] ? 'var(--c-success)' : 'var(--c-text-muted)' }"
                  aria-hidden="true"
                />
                {{ display(row[c] ?? null, column) }}
              </span>
              <template v-else>{{ display(row[c] ?? null, column) }}</template>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <Pagination
      v-if="result.reachable > result.pageSize"
      :page="result.page"
      :page-size="result.pageSize"
      :total="result.reachable"
      :loading="loading"
      @update:page="(p) => emit('page', p)"
    />
  </div>
</template>
