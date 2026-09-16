<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import Card from '@/design-system/Card.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useFreshness, useWorkerHealth } from '@/features/imports/useImportQueries'
import { useKpiSummary } from '@/api/dashboard'
import { useTacVersions } from '@/features/imports/useTacVersions'
import { formatDate, formatDateTime, formatRelative } from '@/lib/format'

/**
 * How current the dashboard's data is.
 *
 * This sits at the top of the dashboard rather than buried in the Import Center, because every
 * figure below it is only as trustworthy as its answer. A vendor share of 41.8% means one thing
 * if it describes yesterday and something quite different if the last successful import was six
 * weeks ago — and nothing else on the page can tell the reader which.
 *
 * Three facts, in the order they change a reader's interpretation: how recent the data is, which
 * days are missing from it, and which TAC mapping the names and models come from.
 */
const freshness = useFreshness()
const health = useWorkerHealth()
const tacVersions = useTacVersions()

const sqm = computed(() => freshness.data.value?.find((f) => f.sourceCode === 'SQM') ?? null)
const activeTac = computed(() => tacVersions.data.value?.find((v) => v.status === 'Active') ?? null)
const pendingTac = computed(() => tacVersions.data.value?.filter((v) => v.status === 'Ready') ?? [])

/*
  The figures on this page and the day named above it come from two different places: the KPI
  marts, and the import history. They agree almost always, and during a mart rebuild they do not
  - the dashboard serves the newest delivery whose marts are complete, which may be a day behind
  what has been imported.

  Saying so is the whole point. A page that shows one delivery's numbers under another
  delivery's date is exactly the kind of quiet wrongness this product is built to avoid.
*/
/*
  Deliberately the unfiltered KPI - the served delivery does not depend on what the reader has
  drilled into - and deliberately spelled the same way the page spells "no filter", so the two
  share one cache entry instead of issuing the identical request twice. `{}` and
  `{ includeUnknownDevice: true }` mean the same thing to the API and are different query keys.
*/
const kpi = useKpiSummary(() => ({ includeUnknownDevice: true }))

const martBehind = computed(() => {
  const served = kpi.data.value?.deliveryDate ?? null
  const imported = sqm.value?.latestBusinessDate ?? null
  if (!imported) return null
  if (served === imported) return null
  return { served, imported }
})

/**
 * How to read the days-behind figure.
 *
 * Thresholds come from the one delivery pattern actually observed: files arrive roughly four
 * weeks behind the day they describe. 35 days is that lag plus a week of slack; past 45 the
 * delivery has stopped rather than slipped. Stated here rather than hidden in a colour, because
 * a threshold nobody can see is a threshold nobody can question.
 */
const lagTone = computed(() => {
  const days = sqm.value?.daysBehind
  if (days === null || days === undefined) return 'var(--c-text-muted)'
  if (days > 45) return 'var(--c-danger)'
  if (days > 35) return 'var(--c-warning)'
  return 'var(--c-success)'
})

const missingSummary = computed(() => {
  const missing = sqm.value?.missingBusinessDates ?? []
  if (!missing.length) return null

  // Newest first from the API; show the range rather than a wall of dates.
  const sorted = [...missing].sort()
  return {
    count: missing.length,
    first: sorted[0]!,
    last: sorted[sorted.length - 1]!,
    all: sorted,
  }
})
</script>

<template>
  <Card>
    <AsyncBoundary
      :is-loading="freshness.isPending.value"
      :is-error="freshness.isError.value"
      :error="freshness.error.value"
      min-height="4rem"
      @retry="freshness.refetch()"
    >
      <div class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-4">
        <!-- Latest day -->
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Data through</dt>
          <dd class="tabular mt-1 text-[var(--text-lg)] font-semibold tracking-tight">
            {{ formatDate(sqm?.latestBusinessDate ?? null) }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)]" :style="{ color: lagTone }">
            <template v-if="sqm?.daysBehind !== null && sqm?.daysBehind !== undefined">
              {{ sqm.daysBehind }} days behind today
            </template>
            <template v-else>no successful import yet</template>
          </dd>
          <dd
            v-if="martBehind"
            class="mt-1 text-[var(--text-xs)] text-[var(--c-warning)]"
            :title="`The marts are being rebuilt. Figures below are from the last complete delivery.`"
          >
            figures below are from
            {{ martBehind.served ? formatDate(martBehind.served) : 'the initial dump' }}
          </dd>
        </div>

        <!-- Missing days -->
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Days not delivered</dt>
          <dd
            class="tabular mt-1 text-[var(--text-lg)] font-semibold tracking-tight"
            :style="{ color: missingSummary ? 'var(--c-warning)' : 'var(--c-success)' }"
          >
            {{ missingSummary?.count ?? 0 }}
          </dd>
          <dd
            class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]"
            :title="missingSummary?.all.join(', ')"
          >
            <template v-if="missingSummary">
              {{ formatDate(missingSummary.first) }} – {{ formatDate(missingSummary.last) }}. Charts
              show gaps, never interpolated points.
            </template>
            <template v-else>Every expected day is present.</template>
          </dd>
        </div>

        <!-- TAC version -->
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Device database</dt>
          <dd class="mt-1 text-[var(--text-lg)] font-semibold tracking-tight">
            {{ activeTac?.versionLabel ?? 'none active' }}
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            <template v-if="pendingTac.length">
              <RouterLink to="/imports" class="font-medium text-[var(--c-warning)] hover:underline">
                {{ pendingTac.length }} version(s) waiting for activation
              </RouterLink>
            </template>
            <template v-else-if="activeTac?.activatedAt">
              activated
              <span :title="formatDateTime(activeTac.activatedAt)">
                {{ formatRelative(activeTac.activatedAt) }}
              </span>
            </template>
            <template v-else>GSMA manufacturer and model mapping.</template>
          </dd>
        </div>

        <!-- Pipeline -->
        <div>
          <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Import pipeline</dt>
          <dd
            class="mt-1 text-[var(--text-lg)] font-semibold tracking-tight"
            :style="{
              color:
                (sqm?.failedLast7Days ?? 0) > 0 ? 'var(--c-danger)' : 'var(--c-text)',
            }"
          >
            <template v-if="(health.data.value?.running ?? 0) > 0">
              {{ health.data.value?.running }} running
            </template>
            <template v-else-if="(sqm?.failedLast7Days ?? 0) > 0">
              {{ sqm?.failedLast7Days }} failed
            </template>
            <template v-else>idle</template>
          </dd>
          <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
            <RouterLink to="/imports" class="hover:text-[var(--c-text)] hover:underline">
              {{ health.data.value?.queued ?? 0 }} queued · open the Import Center
            </RouterLink>
          </dd>
        </div>
      </div>
    </AsyncBoundary>
  </Card>
</template>
