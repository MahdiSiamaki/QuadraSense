<script setup lang="ts">
import { computed, inject } from 'vue'
import { useDailyChanges } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatFull } from '@/lib/format'
import type { PopulationTrendConfig } from './config'
import PopulationTrendChart from './PopulationTrendChart.vue'
import { bucketTrend } from './populationTrend'
import { cutToWindow, datesInWindow, lastDate, seriesGaps } from './shared/series'
import { useFeedSources } from './shared/useFeedSources'
import { useKept } from './shared/useKept'
import { useRefresh } from './shared/useRefresh'

/**
 * Active bindings over time, from the running total the daily-change endpoint already returns: a
 * transform of about 240 rows a year, not a new aggregation (ADR-016 §12).
 *
 * Network-wide: the daily marts carry no vendor or device breakdown, so the dashboard's filters do not
 * apply (the frame says so on the card). The date range does.
 */
const props = defineProps<WidgetViewProps<PopulationTrendConfig>>()
type Snapshot = SnapshotData<'population.trend'>

const active = useQueryActive(props)
const query = useDailyChanges({ enabled: active })
const changes = useWidgetData(props, query, (raw) => (raw as Snapshot).changes)
const { value: rows, loading, updating } = useKept(changes)

const config = computed(() => props.instance.config)
const feed = useFeedSources(props, {
  flagged: () => config.value.showFlagged !== false,
  missing: () => config.value.showMissing !== false,
  snapshot: (raw) => raw as Snapshot,
})
useRefresh([{ enabled: () => active.value, refetch: query.refetch }, ...feed.refreshSources])

const context = inject(DASHBOARD_CONTEXT, null)
const dataThrough = computed(() => context?.dataThrough.value ?? lastDate(rows.value ?? []))
const window = computed(() => resolveRange(props.lens.range, dataThrough.value))
const shown = computed(() => cutToWindow(rows.value ?? [], window.value))

const grain = computed(() => config.value.grain ?? 'day')
const buckets = computed(() => bucketTrend(shown.value, grain.value, feed.flagged.value))

const coverage = computed(() => {
  const r = shown.value
  return r.length ? { from: r[0]!.date, to: r[r.length - 1]!.date, days: r.length } : null
})

const GRAIN_WORDS = { day: 'by day', week: 'by ISO week (Monday to Sunday)', month: 'by calendar month' } as const

/**
 * Days missing inside what is drawn: from the import calendar when the reader may see it, otherwise
 * the holes in the series itself - never "every day was delivered" on no evidence.
 */
const missing = computed(() => {
  if (config.value.showMissing === false || !coverage.value) return null
  const fromCalendar = feed.missingDays.value
  if (fromCalendar === null) return seriesGaps(shown.value)
  const w = window.value
  return w.from === null && w.to === null ? fromCalendar : datesInWindow(fromCalendar, w)
})

const flaggedShown = computed(() => shown.value.filter((r) => feed.flagged.value.has(r.date)).length)
const flaggedBuckets = computed(() => buckets.value.filter((b) => b.flaggedDays.length > 0).length)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Active bindings - number + SIM + handset combinations, not subscribers. The line is the running
      total anchored to the population measured at the latest refreshed delivery and walked back by each
      day's net, so its last day matches the network-wide Active bindings figure.
      <template v-if="coverage">
        {{ formatFull(coverage.days) }} {{ coverage.days === 1 ? 'day' : 'days' }}, {{ coverage.from }} to
        {{ coverage.to }}, {{ GRAIN_WORDS[grain] }}.
      </template>
    </p>

    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="changes.isError.value"
      :error="changes.error.value"
      :is-empty="!loading && shown.length === 0"
      :empty-message="(rows?.length ?? 0) === 0 ? 'No daily files loaded yet.' : 'No delivered day in this date range.'"
      min-height="12rem"
      gap="none"
      @retry="changes.refetch()"
    >
      <div class="relative min-h-0 flex-1 transition-opacity duration-150" :class="updating ? 'opacity-70' : ''">
        <PopulationTrendChart
          :buckets="buckets"
          :grain="grain"
          :chart="config.chart ?? 'line'"
          :flagged="feed.flagged.value"
          height="100%"
        />
      </div>
    </AsyncBoundary>

    <div
      v-if="coverage && (missing !== null || flaggedShown > 0)"
      class="shrink-0 space-y-1.5 text-2xs text-[var(--c-text-muted)]"
    >
      <p v-if="missing !== null">
        <template v-if="missing.length && grain === 'day'">
          <strong>{{ formatFull(missing.length) }} {{ missing.length === 1 ? 'day is' : 'days are' }} missing</strong>
          from the source and appear as gaps rather than interpolated points: {{ missing.join(', ') }}.
        </template>
        <template v-else-if="missing.length">
          <strong>{{ formatFull(missing.length) }} {{ missing.length === 1 ? 'day is' : 'days are' }} missing</strong>
          from the source: {{ missing.join(', ') }}. A {{ grain }} with no delivered day is a gap; any other
          {{ grain }} shows its last delivered day.
        </template>
        <template v-else>Every expected day in this range was delivered and imported.</template>
      </p>
      <p v-if="flaggedShown > 0 && grain === 'day'">
        <strong>{{ formatFull(flaggedShown) }} shaded {{ flaggedShown === 1 ? 'day is one' : 'days are ones' }}</strong>
        whose file looked unlike the ordinary days - IMEIs shifted by a digit, or SIMs carrying several
        numbers. Their figures may be distorted; hover a day for what was found.
      </p>
      <p v-else-if="flaggedShown > 0">
        <strong>{{ formatFull(flaggedBuckets) }} shaded {{ flaggedBuckets === 1 ? grain : `${grain}s` }}</strong>
        contain {{ formatFull(flaggedShown) }} {{ flaggedShown === 1 ? 'day' : 'days' }} whose file looked unlike
        the ordinary days - IMEIs shifted by a digit, or SIMs carrying several numbers. Their figures may be
        distorted; hover a {{ grain }} for what was found.
      </p>
    </div>
  </div>
</template>
