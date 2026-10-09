<script setup lang="ts">
import { computed, inject } from 'vue'
import { RouterLink } from 'vue-router'
import { CHECK_LABELS, useFeedQuality } from '@/api/quality'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { useFreshness } from '@/features/imports/useImportQueries'
import { formatDate, formatFull, formatPercent } from '@/lib/format'
import type { FlaggedDaysConfig } from './config'
import { datesInWindow, lastDate } from '../overview/shared/series'
import { useKept } from '../overview/shared/useKept'

/**
 * The days to be careful with, newest first: days whose file looked unlike the ordinary days, with
 * what was out of line, and days the source never delivered, from the platform's calendar of expected
 * days. Both in the dashboard's date range.
 */
const props = defineProps<WidgetViewProps<FlaggedDaysConfig>>()
type Snapshot = SnapshotData<'quality.flaggedDays'>

const config = computed(() => props.instance.config)
const wantFlagged = computed(() => config.value.include.includes('flagged'))
const wantMissing = computed(() => config.value.include.includes('missing'))
const active = useQueryActive(props)

const feedQuery = useFeedQuality({ enabled: () => active.value && wantFlagged.value })
const freshnessQuery = useFreshness({ enabled: () => active.value && wantMissing.value })
const feed = useWidgetData(props, feedQuery, (raw) => (raw as Snapshot).feedQuality)
const freshness = useWidgetData(props, freshnessQuery, (raw) => (raw as Snapshot).freshness)
const keptFeed = useKept(feed)
const keptFreshness = useKept(freshness)

const context = inject(DASHBOARD_CONTEXT, null)
const days = computed(() => keptFeed.value.value?.days ?? [])
const window = computed(() => resolveRange(props.lens.range, context?.dataThrough.value ?? lastDate(days.value)))
const inWindow = (date: string) =>
  (window.value.from === null || date >= window.value.from) && (window.value.to === null || date <= window.value.to)

interface Entry {
  date: string
  kind: 'flagged' | 'missing'
  what: string[]
  rows: number | null
}

const entries = computed<Entry[]>(() => {
  const out: Entry[] = []
  if (wantFlagged.value) {
    for (const d of days.value) {
      if (!d.flagged || !inWindow(d.date)) continue
      out.push({
        date: d.date,
        kind: 'flagged',
        rows: d.rows,
        what: d.findings
          .filter((f) => f.flagged)
          .map((f) => `${CHECK_LABELS[f.check]}: ${formatPercent(f.rate * 100, 2)} (ordinary ${f.referenceMedian === null ? '—' : formatPercent(f.referenceMedian * 100, 2)})`),
      })
    }
  }
  if (wantMissing.value) {
    const missing = keptFreshness.value.value?.find((f) => f.sourceCode === 'SQM')?.missingBusinessDates ?? []
    for (const date of datesInWindow(missing, window.value)) out.push({ date, kind: 'missing', rows: null, what: ['Never delivered by the source.'] })
  }
  return out.sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : 0))
})

const shown = computed(() => entries.value.slice(0, config.value.limit))
const loading = computed(() => (wantFlagged.value && keptFeed.loading.value) || (wantMissing.value && keptFreshness.loading.value))
const failed = computed(() => (wantFlagged.value && feed.isError.value ? feed : wantMissing.value && freshness.isError.value ? freshness : null))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="keptFeed.updating.value || keptFreshness.updating.value">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      <template v-if="wantFlagged">Days whose file looked unlike the ordinary days</template><template v-if="wantFlagged && wantMissing">, and </template><template v-if="wantMissing">{{ wantFlagged ? 'days' : 'Days' }} the source never delivered</template>, newest first.
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="failed !== null"
      :error="failed?.error.value"
      :is-empty="!loading && shown.length === 0"
      empty-message="Every day in this range was delivered and looked ordinary."
      min-height="6rem"
      gap="none"
      @retry="failed?.refetch()"
    >
      <ul class="min-h-0 flex-1 divide-y overflow-auto overscroll-contain text-xs">
        <li v-for="e in shown" :key="`${e.kind}:${e.date}`" class="flex gap-3 py-1.5">
          <span class="tabular w-24 shrink-0 whitespace-nowrap">{{ formatDate(e.date) }}</span>
          <span class="min-w-0 flex-1">
            <span
              class="mr-1.5 inline-flex rounded-full px-1.5 text-2xs font-medium"
              :class="e.kind === 'flagged' ? 'bg-[var(--c-warning-subtle)] text-[var(--c-warning-text)]' : 'bg-[var(--c-surface-sunken)] text-[var(--c-text-secondary)]'"
            >
              {{ e.kind === 'flagged' ? 'Out of line' : 'Missing' }}
            </span>
            <span v-for="w in e.what" :key="w" class="block text-[var(--c-text-secondary)]">{{ w }}</span>
          </span>
          <span v-if="e.rows !== null" class="tabular shrink-0 text-2xs text-[var(--c-text-muted)]">{{ formatFull(e.rows) }} rows</span>
        </li>
      </ul>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      <template v-if="entries.length > shown.length">The newest {{ shown.length }} of {{ formatFull(entries.length) }}. </template>
      The files themselves are in the
      <RouterLink v-if="mode === 'view'" to="/imports" class="text-[var(--c-accent)] hover:underline">Import Center</RouterLink><template v-else>Import Center</template>.
    </p>
  </div>
</template>
