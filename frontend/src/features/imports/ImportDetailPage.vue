<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Button from '@/design-system/Button.vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import Card from '@/design-system/Card.vue'
import Modal from '@/design-system/Modal.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import StatusBadge from '@/design-system/StatusBadge.vue'
import ImportProgressPanel from './ImportProgressPanel.vue'
import EventTimeline from '@/design-system/EventTimeline.vue'
import FilePreview from './FilePreview.vue'
import {
  useImportDetail,
  useQuarantineSamples,
  useImportActions,
  useStageDurations,
} from './useImportQueries'
import { isTerminal, isRunning } from '@/api/imports'
import { ApiError } from '@/api/client'
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
const router = useRouter()
const jobId = computed(() => Number(route.params['jobId']))

const detail = useImportDetail(jobId)
const { cancel, reprocess } = useImportActions()

const openRule = ref<number | null>(null)

// The page is reused when a lineage link leads to another job, and an open rule belongs to the
// job it was opened on: carried over, it asked the new job for a summary id that is not its own.
watch(jobId, () => {
  openRule.value = null
})
const showPreview = ref(false)
const samples = useQuarantineSamples(jobId, openRule)

const summary = computed(() => detail.data.value?.summary)
const typicals = useStageDurations(() => summary.value?.sourceCode)

/** "SQM · Oct 3, 2026 · 337.0 MB". A file with no business day leaves the day out, not a dash. */
const facts = computed(() => {
  const s = summary.value
  if (!s) return ''
  return [s.sourceCode, s.businessDate ? formatDate(s.businessDate) : null, formatBytes(s.fileBytes)]
    .filter(Boolean)
    .join(' · ')
})
const running = computed(() => (summary.value ? !isTerminal(summary.value.status) : false))

/*
 * Cancel. Offered while the server says it would be taken (`cancellable`), and asked about first:
 * stopping a running import removes what it loaded, and a file replacing a day already in the
 * system has removed that day before loading - so the day stays empty until it is imported
 * again. Past the point where stopping would leave work half-done the button gives way to a line
 * saying why; a cancel asked for shows as stopping until the worker has acted on it.
 */
const confirmingCancel = ref(false)
const stopping = computed(() => !!summary.value?.cancelRequested && running.value)
const pastCancelPoint = computed(
  () => !!summary.value && running.value && !summary.value.cancellable && !stopping.value,
)
const waiting = computed(() => !!summary.value && running.value && !isRunning(summary.value.status))
const cancelError = computed(() => {
  const error = cancel.error.value
  if (!error) return null
  return error instanceof ApiError ? (error.problem?.detail ?? error.message) : 'The cancel was not taken.'
})

function confirmCancel() {
  cancel.mutate(jobId.value, { onSettled: () => (confirmingCancel.value = false) })
}

/** "Import again" opens the import it queued, rather than leaving the reader on the old one. */
function importAgain() {
  reprocess.mutate(jobId.value, {
    onSuccess: (queued) => void router.push(`/imports/${queued.jobId}`),
  })
}

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
      class="inline-flex items-center gap-1 text-xs font-medium text-[var(--c-text-secondary)] hover:text-[var(--c-text)]"
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
              <h1 class="text-lg font-semibold tracking-tight wrap-anywhere">
                {{ summary.originalFileName }}
              </h1>
              <StatusBadge :status="summary.status" />
              <span
                v-if="summary.status === 'Completed' && summary.warningCount > 0"
                class="text-2xs font-medium text-[var(--c-warning-text)]"
                title="Every row was imported. Validation raised warnings on some of them - open the import for the rows and the rules."
              >
                with warnings
              </span>
              <span
                v-if="summary.isEffective"
                class="rounded-full bg-[var(--c-success-subtle)] px-2 py-0.5 text-2xs font-medium text-[var(--c-success-text)]"
                title="This import is the one currently in effect for its business day."
              >
                In effect
              </span>
            </div>
            <!-- wrap-anywhere: a user name has no length limit and must not widen a phone's page. -->
            <p class="mt-1 text-sm wrap-anywhere text-[var(--c-text-secondary)]">
              {{ facts }} · uploaded by {{ summary.createdBy }}
            </p>
          </div>

          <div class="flex shrink-0 gap-2">
            <Button
              v-if="running && summary.cancellable && !stopping"
              variant="danger"
              size="sm"
              @click="confirmingCancel = true"
            >
              Cancel
            </Button>
            <Button v-if="stopping" variant="danger" size="sm" pending>Stopping…</Button>
            <Button
              v-if="!running && detail.data.value.isBlobPresent"
              size="sm"
              :pending="reprocess.isPending.value"
              @click="importAgain"
            >
              Import again
            </Button>
          </div>
        </header>

        <p
          v-if="pastCancelPoint"
          class="rounded-[var(--radius-md)] border px-3 py-2 text-xs text-[var(--c-text-secondary)]"
        >
          {{
            summary.sourceCode === 'TAC'
              ? 'This import is registering its TAC version, so it can no longer be cancelled. Once it finishes, the version waits for an administrator to activate it - or to leave it inactive.'
              : 'This import has loaded its data and is folding it into the history and the dashboards, so it can no longer be cancelled: stopping now would leave them half-updated. Once it finishes, the file can be imported again.'
          }}
        </p>
        <p
          v-if="cancelError"
          class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] px-3 py-2 text-xs text-[var(--c-danger-text)]"
          role="alert"
        >
          {{ cancelError }}
        </p>

        <Modal
          :open="confirmingCancel"
          title="Cancel this import?"
          size="sm"
          :busy="cancel.isPending.value"
          @close="confirmingCancel = false"
        >
          <p v-if="waiting" class="text-sm">
            It has not started, so it simply will not run. The file stays stored and can be
            imported again later.
          </p>
          <p v-else class="text-sm">
            It stops within a few seconds, and whatever it has loaded so far is removed. If this file
            is replacing a day that was already loaded, that day stays empty until a file for it is
            imported again.
          </p>

          <template #actions>
            <Button :disabled="cancel.isPending.value" @click="confirmingCancel = false">
              Keep it
            </Button>
            <Button variant="danger" :pending="cancel.isPending.value" @click="confirmCancel">
              Cancel the import
            </Button>
          </template>
        </Modal>

        <!-- Live progress. Only while something is actually happening; a finished job showing a
             full progress bar adds nothing the status badge has not already said. -->
        <Card v-if="running">
          <ImportProgressPanel
            :summary="summary"
            :progress="detail.data.value.progress"
            :typicals="typicals.data.value"
          />
        </Card>

        <div
          v-if="summary.errorSummary"
          class="rounded-[var(--radius-md)] border border-[var(--c-danger)] bg-[var(--c-danger-subtle)] p-4"
          role="alert"
        >
          <p class="text-sm font-semibold">Why it failed</p>
          <p class="mt-1 text-sm wrap-anywhere">{{ summary.errorSummary }}</p>
        </div>

        <!-- Counters -->
        <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          <Card v-for="counter in counters" :key="counter.label">
            <p class="text-xs text-[var(--c-text-muted)]">{{ counter.label }}</p>
            <p
              class="tabular mt-0.5 text-lg font-semibold tracking-tight"
              :style="counter.warn ? { color: 'var(--c-warning-text)' } : undefined"
            >
              {{ counter.value }}
            </p>
          </Card>
        </div>

        <!--
          minmax(0, 1fr) and min-w-0: a bare 1fr track is at least as wide as its content, so the
          preview of a 26-column TAC file widened the column instead of scrolling inside its card.
        -->
        <div class="grid gap-5 lg:grid-cols-[minmax(0,1fr)_20rem]">
          <div class="min-w-0 space-y-5">
            <Card title="The file as delivered" subtitle="The first rows, exactly as they arrived">
              <template #actions>
                <button
                  type="button"
                  class="text-xs font-medium text-[var(--c-accent)] hover:underline"
                  :aria-expanded="showPreview"
                  @click="showPreview = !showPreview"
                >
                  {{ showPreview ? 'Hide' : 'Show first rows' }}
                </button>
              </template>
              <FilePreview :job-id="jobId" :open="showPreview" />
              <p v-if="!showPreview" class="text-xs text-[var(--c-text-muted)]">
                Not loaded until asked for — these files reach a gigabyte.
              </p>
            </Card>

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
                      <span class="block text-sm font-medium">
                        {{ group.message }}
                      </span>
                      <span class="block text-2xs text-[var(--c-text-muted)]">
                        {{ group.ruleCode
                        }}<template v-if="group.columnName"> · column {{ group.columnName }}</template>
                        <template v-if="group.firstRowNumber">
                          · first at line {{ formatFull(group.firstRowNumber) }}</template
                        >
                      </span>
                    </span>
                    <span class="tabular shrink-0 text-sm font-semibold">
                      {{ formatFull(group.occurrenceCount) }}
                    </span>
                  </button>

                  <div v-if="openRule === group.summaryId" class="px-4 pb-3">
                    <p v-if="samples.isPending.value" class="text-xs text-[var(--c-text-muted)]">
                      Loading examples…
                    </p>
                    <!-- An error is not an absence: "no examples" would misreport a failed request. -->
                    <p v-else-if="samples.isError.value" class="text-xs text-[var(--c-danger-text)]">
                      The examples could not be loaded.
                      <button type="button" class="underline" @click="samples.refetch()">Try again</button>
                    </p>
                    <table v-else-if="samples.data.value?.length" class="w-full text-2xs">
                      <thead class="text-left text-[var(--c-text-muted)]">
                        <tr>
                          <th scope="col" class="py-1 pr-3 font-medium">Line</th>
                          <th scope="col" class="py-1 pr-3 font-medium">Row as delivered</th>
                          <th scope="col" class="py-1 font-medium">Offending value</th>
                        </tr>
                      </thead>
                      <tbody class="font-mono">
                        <tr v-for="sample in samples.data.value" :key="sample.rowNumber" class="border-t">
                          <td class="tabular py-1 pr-3 align-top">{{ formatFull(sample.rowNumber) }}</td>
                          <td class="py-1 pr-3 align-top wrap-anywhere">{{ sample.rawLine }}</td>
                          <td class="py-1 align-top wrap-anywhere">{{ sample.offendingValue ?? '—' }}</td>
                        </tr>
                      </tbody>
                    </table>
                    <p v-else class="text-xs text-[var(--c-text-muted)]">
                      No examples were retained for this rule.
                    </p>
                  </div>
                </li>
              </ul>
            </Card>
          </div>

          <!-- File facts and lineage -->
          <Card title="File">
            <dl class="space-y-3 text-xs">
              <div>
                <dt class="text-[var(--c-text-muted)]">Content hash (SHA-256)</dt>
                <dd class="mt-0.5 font-mono wrap-anywhere">
                  {{ detail.data.value.sha256 }}
                </dd>
                <dd class="mt-0.5 text-2xs text-[var(--c-text-muted)]">
                  A file is identified by its content, never its name. This hash is what makes a
                  re-upload detectable.
                </dd>
              </div>

              <div>
                <dt class="text-[var(--c-text-muted)]">Stored at</dt>
                <dd class="mt-0.5 font-mono wrap-anywhere">
                  {{ detail.data.value.storedPath }}
                  <span v-if="!detail.data.value.isBlobPresent" class="text-[var(--c-danger-text)]">
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
                  <p v-if="detail.data.value.supersededByJobId" class="text-[var(--c-warning-text)]">
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
