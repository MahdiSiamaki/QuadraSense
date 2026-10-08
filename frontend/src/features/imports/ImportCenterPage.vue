<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Pagination from '@/design-system/Pagination.vue'
import UploadDropzone from './UploadDropzone.vue'
import ImportHistoryTable from './ImportHistoryTable.vue'
import TacVersionPanel from './TacVersionPanel.vue'
import { RouterLink } from 'vue-router'
import { useBlockages, useImportHistory, useWorkerHealth, useFreshness } from './useImportQueries'
import { formatDate, formatRelative, formatDateTime } from '@/lib/format'
import type { ImportHistoryQuery } from '@/api/imports'

/**
 * Import Center: bring files in, and see what happened to the ones already brought in.
 *
 * The layout puts state before action, which is the reverse of the obvious arrangement. An
 * operator arriving here usually wants to know whether yesterday landed, not to upload
 * something — and if a day is missing, that changes which file they reach for. Upload sits in
 * the right-hand column, always visible, never the first thing read.
 */
const source = ref<'' | 'SQM' | 'TAC'>('')
const statusFilter = ref('')
const fileName = ref('')
const page = ref(1)

const query = computed<ImportHistoryQuery>(() => ({
  source: source.value || undefined,
  status: statusFilter.value || undefined,
  fileName: fileName.value || undefined,
  page: page.value,
  pageSize: 25,
}))

const history = useImportHistory(query)
const health = useWorkerHealth()
const blockages = useBlockages()
const freshness = useFreshness()


/** Status groupings an operator actually filters by, not the seventeen raw statuses. */
const STATUS_PRESETS = [
  { value: '', label: 'All' },
  { value: 'Completed,PartiallyCompleted', label: 'Succeeded' },
  { value: 'Failed,Quarantined', label: 'Failed' },
  // Every non-terminal status. Deduplicating (a daily file) and Enriching (a TAC file) were
  // missing, so a job in either stage disappeared from "In flight" while it was running.
  {
    value:
      'Uploaded,Queued,Retrying,Validating,Parsing,Normalizing,Deduplicating,Enriching,Importing,Aggregating,Finalizing',
    label: 'In flight',
  },
  { value: 'Duplicate', label: 'Duplicates' },
]

function resetPaging() {
  page.value = 1
}
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Import Center</h1>
        <p class="mt-0.5 text-sm text-[var(--c-text-secondary)]">
          Daily subscriber changes and GSMA TAC snapshots. Every file is kept, every import is
          repeatable, and re-importing a day replaces it rather than adding to it.
        </p>
      </div>

      <!-- Worker state, stated plainly. A queue that is not draining is the single most
           useful thing to notice on this page, and it should not need a click. -->
      <div v-if="health.data.value" class="flex items-center gap-4 text-xs">
        <span class="flex items-center gap-1.5">
          <span
            class="size-1.5 rounded-full"
            :class="health.data.value.activeWorkers > 0 ? 'animate-pulse' : ''"
            :style="{
              backgroundColor:
                health.data.value.activeWorkers > 0 ? 'var(--c-success)' : 'var(--c-text-muted)',
            }"
            aria-hidden="true"
          />
          <span class="text-[var(--c-text-secondary)]">
            {{ health.data.value.activeWorkers }} worker{{ health.data.value.activeWorkers === 1 ? '' : 's' }}
            <template v-if="health.data.value.workerHostedInApi">(in the API)</template>
          </span>
        </span>
        <span class="text-[var(--c-text-secondary)]">
          {{ health.data.value.queued }} queued · {{ health.data.value.running }} running
        </span>
        <span v-if="health.data.value.staleLeases > 0" class="font-medium text-[var(--c-danger-text)]">
          {{ health.data.value.staleLeases }} stale lease(s)
        </span>
      </div>
    </header>

    <!--
      The state that used to be silent.

      A 319.6 MB file was uploaded successfully and sat at "Queued" forever, because no worker
      process was running. Every component reported success; nothing said why nothing happened.
      This is the one combination worth interrupting the page for: work waiting, and nothing
      alive to take it.
    -->
    <div
      v-if="health.data.value?.stalled"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ health.data.value.queued }} job{{ health.data.value.queued === 1 ? '' : 's' }} queued,
        and no import worker is running.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        Nothing will be imported until one starts. In development the API hosts the worker itself,
        so this usually means the API was started with
        <code class="text-xs">Import:RunWorkerInProcess=false</code>, or the worker
        process has stopped. Start one with:
      </p>
      <pre
        class="mt-2 overflow-x-auto rounded-[var(--radius-md)] bg-[var(--c-surface-sunken)] px-3 py-2 text-xs"
      ><code>dotnet run --project backend/src/Sqm.Ingestion</code></pre>
      <p
        v-if="health.data.value.oldestQueuedAt"
        class="mt-2 text-2xs text-[var(--c-text-muted)]"
      >
        The oldest has been waiting since {{ formatDateTime(health.data.value.oldestQueuedAt) }}.
      </p>
    </div>

    <!--
      The other silent state. Five days were uploaded and sat at "Queued" with a worker running,
      because an earlier day had failed: days of a source import in order, so that none is folded
      over a gap. The rule is right; a wait nobody can see the reason for looks like a broken worker.
    -->
    <div
      v-for="b in blockages.data.value ?? []"
      :key="`${b.sourceCode}:${b.businessDate}`"
      class="rounded-[var(--radius-md)] border border-[var(--c-warning)] p-4"
      role="alert"
    >
      <p class="text-sm font-semibold">
        {{ b.waiting }} {{ b.sourceCode }} file{{ b.waiting === 1 ? ' is' : 's are' }} waiting behind
        {{ formatDate(b.businessDate) }}, whose import {{ b.status === 'CANCELLED' ? 'was cancelled' : 'failed' }}.
      </p>
      <p class="mt-1 text-sm text-[var(--c-text-secondary)]">
        Days of a source are imported in order, so that no day is applied over a gap. Open that day and
        choose <span class="font-medium">Import again</span>; the waiting files follow on their own.
      </p>
      <RouterLink :to="`/imports/${b.jobId}`" class="mt-2 inline-block text-sm font-medium text-[var(--c-accent)] hover:underline">
        Open {{ formatDate(b.businessDate) }}
      </RouterLink>
    </div>

    <!-- Freshness, per source. Placed above the fold because "is the data current?" is the
         question this whole page exists to answer. -->
    <div v-if="freshness.data.value?.length" class="grid gap-3 sm:grid-cols-2">
      <Card v-for="row in freshness.data.value" :key="row.sourceCode">
        <div class="flex items-start justify-between gap-4">
          <div class="min-w-0">
            <p class="text-xs font-medium text-[var(--c-text-muted)]">
              {{ row.sourceCode }}
            </p>
            <p class="mt-0.5 text-lg font-semibold tracking-tight">
              {{ formatDate(row.latestBusinessDate) }}
            </p>
            <p class="text-xs text-[var(--c-text-secondary)]">
              latest day imported<template v-if="row.latestImportedAt">
                · loaded
                <span :title="formatDateTime(row.latestImportedAt)">
                  {{ formatRelative(row.latestImportedAt) }}
                </span>
              </template>
            </p>
          </div>

          <div class="shrink-0 space-y-1 text-right">
            <p
              v-if="row.daysBehind !== null"
              class="text-xs font-medium"
              :style="{
                color:
                  row.daysBehind > 45
                    ? 'var(--c-danger-text)'
                    : row.daysBehind > 35
                      ? 'var(--c-warning-text)'
                      : 'var(--c-text-secondary)',
              }"
            >
              {{ row.daysBehind }} days behind
            </p>
            <p
              v-if="row.missingBusinessDates.length"
              class="text-xs text-[var(--c-warning-text)]"
              :title="row.missingBusinessDates.slice(0, 20).join(', ')"
            >
              {{ row.missingBusinessDates.length }} day(s) missing
            </p>
            <p v-if="row.failedLast7Days" class="text-xs text-[var(--c-danger-text)]">
              {{ row.failedLast7Days }} failed this week
            </p>
          </div>
        </div>
      </Card>
    </div>

    <!-- minmax(0, 1fr): the history table scrolls inside its card instead of widening the column. -->
    <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <!-- History -->
      <Card title="Import history" flush>
        <template #actions>
          <div class="flex flex-wrap items-center gap-2">
            <input
              v-model="fileName"
              type="search"
              placeholder="Filter by file name"
              class="w-44 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              @input="resetPaging"
            />
            <select
              v-model="source"
              class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              aria-label="Data source"
              @change="resetPaging"
            >
              <option value="">All sources</option>
              <option value="SQM">SQM</option>
              <option value="TAC">TAC</option>
            </select>
            <select
              v-model="statusFilter"
              class="rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs"
              aria-label="Status"
              @change="resetPaging"
            >
              <option v-for="preset in STATUS_PRESETS" :key="preset.label" :value="preset.value">
                {{ preset.label }}
              </option>
            </select>
          </div>
        </template>

        <AsyncBoundary
          gap="none"
          :is-loading="history.isPending.value"
          :is-error="history.isError.value"
          :error="history.error.value"
          :is-empty="history.data.value?.items.length === 0"
          empty-message="Nothing has been imported yet."
          min-height="20rem"
          @retry="history.refetch()"
        >
          <ImportHistoryTable :rows="history.data.value?.items ?? []" />
          <!-- The shared pager: the same range, keys and press as every other list. -->
          <Pagination
            v-if="history.data.value"
            :page="page"
            :page-size="history.data.value.pageSize"
            :total="history.data.value.total"
            :loading="history.isFetching.value"
            @update:page="page = $event"
          />
        </AsyncBoundary>

      </Card>

      <!-- Upload -->
      <div class="space-y-5">
        <Card title="Daily subscriber changes" subtitle="One file per day, named with its date">
          <UploadDropzone
            source-code="SQM"
            hint="daily_subs_device_sim_info_YYYY-MM-DD.csv"
            @uploaded="history.refetch()"
          />
        </Card>

        <Card title="GSMA TAC database" subtitle="A full snapshot; activated separately">
          <UploadDropzone
            source-code="TAC"
            hint="Uploading does not change the dashboard. An administrator activates the version."
            @uploaded="history.refetch()"
          />
        </Card>

        <TacVersionPanel />
      </div>
    </div>
  </div>
</template>
