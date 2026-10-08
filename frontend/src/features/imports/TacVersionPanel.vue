<script setup lang="ts">
import { computed, ref } from 'vue'
import Card from '@/design-system/Card.vue'
import { useTacVersions, useActivateTacVersion, type TacVersion } from './useTacVersions'
import { formatDate, formatDateTime, formatFull, formatRelative } from '@/lib/format'
import { ApiError } from '@/api/client'

/**
 * TAC versions, and the decision to activate one.
 *
 * The panel exists because activation is deliberately not automatic. An uploaded GSMA snapshot
 * is loaded, checked and diffed, and then stops — TAC decides the manufacturer and model shown
 * on every screen, so a bad file that activated itself would be wrong everywhere at once.
 *
 * What makes the decision possible rather than a leap of faith is the diff: how many TACs the
 * file adds, removes and changes, against a measured norm of 1,000–1,650 a month. A figure far
 * outside that is stated as such rather than left for the reader to notice.
 */
const versions = useTacVersions()
const activate = useActivateTacVersion()

const confirming = ref<number | null>(null)
const result = ref<string | null>(null)
const failure = ref<string | null>(null)

/** The observed monthly revision, from the only two data points the project has. */
const EXPECTED_MONTHLY_CHANGE = 1650

function changeTotal(version: TacVersion): number {
  return (version.tacsAdded ?? 0) + (version.tacsRemoved ?? 0) + (version.tacsUpdated ?? 0)
}

function isUnusual(version: TacVersion): boolean {
  return version.diffAgainstId !== null && changeTotal(version) > EXPECTED_MONTHLY_CHANGE * 6
}

const rows = computed(() => versions.data.value ?? [])

async function confirm(id: number) {
  failure.value = null
  result.value = null

  try {
    const response = await activate.mutateAsync(id)
    result.value = response.message
  } catch (error) {
    failure.value =
      error instanceof ApiError
        ? (error.problem?.detail ?? error.message)
        : 'The version could not be activated.'
  } finally {
    confirming.value = null
  }
}

const tone: Record<string, string> = {
  Active: 'var(--c-success-text)',
  Ready: 'var(--c-warning-text)',
  Superseded: 'var(--c-text-muted)',
  Failed: 'var(--c-danger-text)',
  Draft: 'var(--c-text-muted)',
  Processing: 'var(--c-accent)',
}
</script>

<template>
  <Card
    title="Device database versions"
    subtitle="An uploaded GSMA snapshot never changes the dashboard until it is activated here."
    flush
  >
    <p
      v-if="result"
      class="mx-4 mt-3 rounded-[var(--radius-md)] bg-[var(--c-success-subtle)] px-3 py-2 text-xs text-[var(--c-success-text)]"
      role="status"
    >
      {{ result }}
    </p>
    <p
      v-if="failure"
      class="mx-4 mt-3 rounded-[var(--radius-md)] bg-[var(--c-danger-subtle)] px-3 py-2 text-xs text-[var(--c-danger-text)]"
      role="alert"
    >
      {{ failure }}
    </p>

    <ul>
      <li v-for="version in rows" :key="version.id" class="border-b px-4 py-3 last:border-0">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <div class="flex items-center gap-2">
              <span class="text-sm font-semibold">{{ version.versionLabel }}</span>
              <span
                class="rounded-full px-2 py-0.5 text-2xs font-medium"
                :style="{ color: tone[version.status], backgroundColor: 'var(--c-surface-sunken)' }"
              >
                {{ version.status }}
              </span>
            </div>
            <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
              {{ formatFull(version.rowCount ?? 0) }} TACs
              <template v-if="version.datasetDate"> · dated {{ formatDate(version.datasetDate) }}</template>
              <template v-if="version.activatedAt">
                · activated
                <span :title="formatDateTime(version.activatedAt)">
                  {{ formatRelative(version.activatedAt) }}
                </span>
                <template v-if="version.activatedBy"> by {{ version.activatedBy }}</template>
              </template>
            </p>
          </div>

          <div class="flex shrink-0 items-center gap-2">
            <button
              v-if="version.status === 'Ready' || version.status === 'Superseded'"
              type="button"
              class="rounded-[var(--radius-md)] border px-3 py-1.5 text-xs font-medium hover:bg-[var(--c-surface-hover)] disabled:opacity-50"
              :disabled="activate.isPending.value"
              @click="confirming = version.id"
            >
              {{ version.status === 'Superseded' ? 'Roll back to this' : 'Activate' }}
            </button>
          </div>
        </div>

        <!-- The diff. Without it, activation is a leap of faith. -->
        <dl
          v-if="version.diffAgainstId !== null"
          class="mt-2 flex flex-wrap gap-x-6 gap-y-1 text-xs"
        >
          <div class="flex gap-1.5">
            <dt class="text-[var(--c-text-muted)]">added</dt>
            <dd class="tabular font-medium text-[var(--c-success-text)]">
              +{{ formatFull(version.tacsAdded ?? 0) }}
            </dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="text-[var(--c-text-muted)]">removed</dt>
            <dd class="tabular font-medium text-[var(--c-danger-text)]">
              −{{ formatFull(version.tacsRemoved ?? 0) }}
            </dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="text-[var(--c-text-muted)]">changed</dt>
            <dd class="tabular font-medium text-[var(--c-warning-text)]">
              {{ formatFull(version.tacsUpdated ?? 0) }}
            </dd>
          </div>
          <div class="flex gap-1.5">
            <dt class="text-[var(--c-text-muted)]">unchanged</dt>
            <dd class="tabular text-[var(--c-text-secondary)]">
              {{ formatFull(version.tacsUnchanged ?? 0) }}
            </dd>
          </div>
        </dl>

        <p v-if="isUnusual(version)" class="mt-1.5 text-xs text-[var(--c-warning-text)]">
          {{ formatFull(changeTotal(version)) }} TACs changed. A month's revision has been
          1,000–1,650 — this is far outside that, so it is worth confirming this is the intended
          file before activating it.
        </p>

        <!-- Confirmation is inline and states the consequence, not a generic "are you sure". -->
        <div
          v-if="confirming === version.id"
          class="mt-3 rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] p-3"
        >
          <p class="text-xs">
            Activating <strong>{{ version.versionLabel }}</strong> changes the manufacturer, model
            and capability shown for every device across the whole product. The version currently
            active becomes superseded and can be rolled back to from this list.
          </p>
          <div class="mt-2 flex gap-2">
            <button
              type="button"
              class="rounded-[var(--radius-md)] bg-[var(--c-accent)] px-3 py-1.5 text-xs font-medium text-[var(--c-accent-text)] disabled:opacity-50"
              :disabled="activate.isPending.value"
              @click="confirm(version.id)"
            >
              {{ activate.isPending.value ? 'Activating…' : 'Yes, activate it' }}
            </button>
            <button
              type="button"
              class="rounded-[var(--radius-md)] border px-3 py-1.5 text-xs font-medium"
              @click="confirming = null"
            >
              Cancel
            </button>
          </div>
        </div>
      </li>
    </ul>

    <p
      v-if="!rows.length"
      class="px-4 py-6 text-center text-sm text-[var(--c-text-muted)]"
    >
      No TAC versions recorded yet.
    </p>
  </Card>
</template>
