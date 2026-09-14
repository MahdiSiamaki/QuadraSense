<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import StatusBadge from '@/design-system/StatusBadge.vue'
import ProgressBar from '@/design-system/ProgressBar.vue'
import EventTimeline from '@/design-system/EventTimeline.vue'
import { useImportDetail, useQuarantineSamples, useImportActions } from './useImportQueries'
import { isTerminal } from '@/api/imports'
import {
  formatBytes,
  formatDate,
  formatDateTime,
  formatDuration,
  formatFull,
} from '@/lib/format'

/**
 * One import, in full.
 *
 * The page answers three questions in the order they get asked: did it work, what happened
 * while it ran, and what exactly was wrong with the rows that did not make it. Lineage — what
 * this import replaced and what replaced it — sits with the file facts, because it is a
 * property of the data, not of the run.
 */
const route = useRoute()
const jobId = computed(() => Number(route.params['jobId']))

const detail = useImportDetail(jobId)
const { cancel, reprocess } = useImportActions()

const openRule = ref<number | null>(null)
const samples = useQuarantineSamples(jobId, openRule)

const summary = computed(() => detail.data.value?.summary)
const running = computed(() => (summary.value ? !isTerminal(summary.value.status) : false))

/** Row counts, only the ones that carry information for this particular job. */
const counters = computed(() => {
  const s = summary.value
  if (!s) return []

  return [
    { label: 'Rows read', value: formatFull(s.rowsInput) },
    { label: 'Rows imported', value: formatFull(s.rowsInserted) },
    ...(s.rowsInvalid > 0 ? [{ label: 'Quarantined', value: formatFull(s.rowsInvalid), warn: true }] : []),
    { label: 'Attempt', value: String(s.attempt) },
    { label: 'Duration', value: formatDuration(s.durationMs) },
  ]
})

function toggleRule(summaryId: number) {
  openRule.value = openRule.value === summaryId ? null : summaryId
}
</script>

<template>
  <div class="space-y-5">
    <RouterLink
      to="/imports"
      class="inline-flex items-center gap-1 text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)] hover:text-[var(--c-text)]"
    >
      ← Import Center
    </RouterLink>

    <AsyncBoundary
      :is-loading="detail.isPending.value"
      :is-error="detail.isError.value"
      :error="detail.error.value"
      min-height="24rem"
      @retry="detail.refetch()"
    >
      <div v-if="detail.data.value && summary" class="space-y-5">
        <!-- Heading -->
        <header class="flex flex-wrap items-start justify-between gap-4">
          <div class="min-w-0">
            <div class="flex flex-wrap items-center gap-2">
              <h1 class="text-[var(--text-lg)] font-semibold tracking-tight wrap-anywhere">
                {{ summary.originalFileName }}
              </h1>
              <StatusBadge :status="summary.status" />
              <span
                v-if="summary.isEffective"
                class="rounded-full bg-[var(--c-success-subtle)] px-2 py-0.5 text-[var(--text-2xs)] font-medium text-[var(--c-success)]"
                title="This import is the one currently in effect for its business day."
              >
                In effect
              </span>
            </div>
            <p class="mt-1 text-[var(--text-sm)] text-[var(--c-text-secondary)]">
              {{ summary.sourceCode }} · {{ formatDate(summary.businessDate) }} ·
              {{ formatBytes(summary.fileBytes) }} · uploaded by {{ summary.createdBy }}
            </p>
          </div>

          <div class="flex shrink-0 gap-2">
            <button
              v-if="running"
              type="button"
              class="rounded-[var(--radius-md)] border border-[var(--c-danger)] px-3 py-1.5 text-[var(--text-xs)] font-medium text-[var(--c-danger)] hover:bg-[var(--c-danger-subtle)] disabled:opacity-50"
              :disabled="cancel.isPending.value"
              @click="cancel.mutate(jobId)"
            >
              Cancel
            </button>
            <button
              v-if="!running && detail.data.value.isBlobPresent"
              type="button"
              class="rounded-[var(--radius-md)] border px-3 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)] disabled:opacity-50"
              :disabled="reprocess.isPending.value"
              @click="reprocess.mutate(jobId)"
            >
              Import again
            </button>
          </div>
        </header>

        <!-- Live progress. Only while something is actually happening; a finished job showing a
             full progress bar adds nothing the status badge has not already said. -->
        <Card v-if="running && detail.data.value.progress">
          <ProgressBar
            :percent="detail.data.value.progress.percent"
            :label="detail.data.value.progress.stage.toLowerCase()"
            :detail="`${formatFull(detail.data.value.progress.rowsProcessed)} of ${
              detail.data.value.progress.rowsExpected
                ? formatFull(detail.data.value.progress.rowsExpected)
                : 'unknown'
            }`"
          />
        </Card>

        <div
          v-if="summary.errorSummary"
          class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
          role="alert"
        >
          <p class="text-[var(--text-sm)] font-semibold">Why it failed</p>
          <p class="mt-1 text-[var(--text-sm)] wrap-anywhere">{{ summary.errorSummary }}</p>
        </div>

        <!-- Counters -->
        <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          <Card v-for="counter in counters" :key="counter.label">
            <p class="text-[var(--text-xs)] text-[var(--c-text-muted)]">{{ counter.label }}</p>
            <p
              class="tabular mt-0.5 text-[var(--text-lg)] font-semibold tracking-tight"
              :style="counter.warn ? { color: 'var(--c-warning)' } : undefined"
            >
              {{ counter.value }}
            </p>
          </Card>
        </div>

        <div class="grid gap-5 lg:grid-cols-[1fr_20rem]">
          <div class="space-y-5">
            <Card title="What happened" subtitle="Recorded by the worker as it ran">
              <EventTimeline :events="detail.data.value.events" />
            </Card>

            <Card
              v-if="detail.data.value.quarantine.length"
              title="Quarantine"
              subtitle="Rows grouped by the rule they broke, with examples"
              flush
            >
              <ul>
                <li
                  v-for="group in detail.data.value.quarantine"
                  :key="group.summaryId"
                  class="border-b last:border-0"
                >
                  <button
                    type="button"
                    class="flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-[var(--c-surface-hover)]"
                    :aria-expanded="openRule === group.summaryId"
                    @click="toggleRule(group.summaryId)"
                  >
                    <span
                      class="mt-1 size-2 shrink-0 rounded-full"
                      :style="{
                        backgroundColor:
                          group.severity === 'error' ? 'var(--c-danger)' : 'var(--c-warning)',
                      }"
                      aria-hidden="true"
                    />
                    <span class="min-w-0 flex-1">
                      <span class="block text-[var(--text-sm)] font-medium">
                        {{ group.message }}
                      </span>
                      <span class="block text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                        {{ group.ruleCode
                        }}<template v-if="group.columnName"> · column {{ group.columnName }}</template>
                        <template v-if="group.firstRowNumber">
                          · first at line {{ formatFull(group.firstRowNumber) }}</template
                        >
                      </span>
                    </span>
                    <span class="tabular shrink-0 text-[var(--text-sm)] font-semibold">
                      {{ formatFull(group.occurrenceCount) }}
                    </span>
                  </button>

                  <div v-if="openRule === group.summaryId" class="px-4 pb-3">
                    <p v-if="samples.isPending.value" class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
                      Loading examples…
                    </p>
                    <table v-else-if="samples.data.value?.length" class="w-full text-[var(--text-2xs)]">
                      <thead class="text-left text-[var(--c-text-muted)]">
                        <tr>
                          <th scope="col" class="py-1 pr-3 font-medium">Line</th>
                          <th scope="col" class="py-1 pr-3 font-medium">Row as delivered</th>
                          <th scope="col" class="py-1 font-medium">Offending value</th>
                        </tr>
                      </thead>
                      <tbody class="font-[var(--font-mono)]">
                        <tr v-for="sample in samples.data.value" :key="sample.rowNumber" class="border-t">
                          <td class="tabular py-1 pr-3 align-top">{{ sample.rowNumber }}</td>
                          <td class="py-1 pr-3 align-top wrap-anywhere">{{ sample.rawLine }}</td>
                          <td class="py-1 align-top wrap-anywhere">{{ sample.offendingValue ?? '—' }}</td>
                        </tr>
                      </tbody>
                    </table>
                    <p v-else class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
                      No examples were retained for this rule.
                    </p>
                  </div>
                </li>
              </ul>
            </Card>
          </div>

          <!-- File facts and lineage -->
          <Card title="File">
            <dl class="space-y-3 text-[var(--text-xs)]">
              <div>
                <dt class="text-[var(--c-text-muted)]">Content hash (SHA-256)</dt>
                <dd class="mt-0.5 font-[var(--font-mono)] wrap-anywhere">
                  {{ detail.data.value.sha256 }}
                </dd>
                <dd class="mt-0.5 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
                  A file is identified by its content, never its name. This hash is what makes a
                  re-upload detectable.
                </dd>
              </div>

              <div>
                <dt class="text-[var(--c-text-muted)]">Stored at</dt>
                <dd class="mt-0.5 font-[var(--font-mono)] wrap-anywhere">
                  {{ detail.data.value.storedPath }}
                  <span v-if="!detail.data.value.isBlobPresent" class="text-[var(--c-danger)]">
                    (deleted)
                  </span>
                </dd>
              </div>

              <div>
                <dt class="text-[var(--c-text-muted)]">Queued</dt>
                <dd class="mt-0.5">{{ formatDateTime(summary.createdAt) }}</dd>
              </div>
              <div v-if="summary.startedAt">
                <dt class="text-[var(--c-text-muted)]">Started</dt>
                <dd class="mt-0.5">{{ formatDateTime(summary.startedAt) }}</dd>
              </div>
              <div v-if="summary.finishedAt">
                <dt class="text-[var(--c-text-muted)]">Finished</dt>
                <dd class="mt-0.5">{{ formatDateTime(summary.finishedAt) }}</dd>
              </div>

              <div
                v-if="
                  detail.data.value.supersedesJobId ||
                  detail.data.value.supersededByJobId ||
                  detail.data.value.reprocessOfJobId
                "
                class="border-t pt-3"
              >
                <dt class="text-[var(--c-text-muted)]">Lineage</dt>
                <dd class="mt-1 space-y-1">
                  <p v-if="detail.data.value.reprocessOfJobId">
                    Re-run of
                    <RouterLink
                      :to="`/imports/${detail.data.value.reprocessOfJobId}`"
                      class="font-medium text-[var(--c-accent)] hover:underline"
                    >
                      import {{ detail.data.value.reprocessOfJobId }}
                    </RouterLink>
                  </p>
                  <p v-if="detail.data.value.supersedesJobId">
                    Replaced
                    <RouterLink
                      :to="`/imports/${detail.data.value.supersedesJobId}`"
                      class="font-medium text-[var(--c-accent)] hover:underline"
                    >
                      import {{ detail.data.value.supersedesJobId }}
                    </RouterLink>
                    for this day
                  </p>
                  <p v-if="detail.data.value.supersededByJobId" class="text-[var(--c-warning)]">
                    Superseded by
                    <RouterLink
                      :to="`/imports/${detail.data.value.supersededByJobId}`"
                      class="font-medium hover:underline"
                    >
                      import {{ detail.data.value.supersededByJobId }}
                    </RouterLink>
                  </p>
                </dd>
              </div>
            </dl>
          </Card>
        </div>
      </div>
    </AsyncBoundary>
  </div>
</template>
