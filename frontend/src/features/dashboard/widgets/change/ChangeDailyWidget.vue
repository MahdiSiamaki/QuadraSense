<script setup lang="ts">
import { computed, inject } from 'vue'
import { useDailyChanges } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import ChangeTimeSeries from '@/design-system/ChangeTimeSeries.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatFull } from '@/lib/format'
import type { ChangeDailyConfig } from './config'
import { cutToWindow, datesInWindow, lastDate, seriesGaps } from '../overview/shared/series'
import { useFeedSources } from '../overview/shared/useFeedSources'
import { useKept } from '../overview/shared/useKept'

/**
 * Each day's adds and removes, and the running total - the dashboard's original daily chart, in the
 * dashboard's date range. Network-wide: the daily marts have no vendor or device breakdown.
 *
 * The missing-day footnote comes from the platform's calendar of expected days when the reader has
 * import.view, and otherwise from the holes in the series itself - never "every day was delivered"
 * without evidence.
 */
const props = defineProps<WidgetViewProps<ChangeDailyConfig>>()
type Snapshot = SnapshotData<'change.daily'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const query = useDailyChanges({ enabled: active })
const changes = useWidgetData(props, query, (raw) => (raw as Snapshot).changes)
const { value: rows, loading, updating } = useKept(changes)

const feed = useFeedSources(props, {
  flagged: () => config.value.showFlagged !== false,
  missing: () => config.value.showMissing !== false,
  snapshot: (raw) => raw as Snapshot,
})

const context = inject(DASHBOARD_CONTEXT, null)
const dataThrough = computed(() => context?.dataThrough.value ?? lastDate(rows.value ?? []))
const window = computed(() => resolveRange(props.lens.range, dataThrough.value))
const shown = computed(() => cutToWindow(rows.value ?? [], window.value))
const whole = computed(() => window.value.from === null && window.value.to === null)

const coverage = computed(() => {
  const r = shown.value
  return r.length ? { from: r[0]!.date, to: r[r.length - 1]!.date, days: r.length } : null
})

const missing = computed(() => {
  if (config.value.showMissing === false || !coverage.value) return null
  const fromCalendar = feed.missingDays.value
  if (fromCalendar === null) return seriesGaps(shown.value)
  return whole.value ? fromCalendar : datesInWindow(fromCalendar, window.value)
})

const flaggedShown = computed(() => shown.value.filter((r) => feed.flagged.value.has(r.date)).length)

const drawsTotal = computed(() => config.value.series.includes('cumulative'))
const drawsFlow = computed(() => config.value.series.some((s) => s !== 'cumulative'))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      <template v-if="coverage">{{ formatFull(coverage.days) }} {{ coverage.days === 1 ? 'day' : 'days' }}, {{ coverage.from }} to {{ coverage.to }}.</template>
      <template v-if="drawsFlow && drawsTotal"> {{ config.chart === 'bar' ? 'Bars are' : 'The thin lines are' }} the day's flow; the line is the running total.</template>
      <template v-else-if="drawsTotal"> The running total of active bindings.</template>
      <template v-else> The day's flow of bindings.</template>
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
        <ChangeTimeSeries
          :data="shown"
          :flagged="feed.flagged.value"
          :series="config.series"
          :chart="config.chart"
          :zoom-start="whole ? 66 : 0"
          height="100%"
        />
      </div>
    </AsyncBoundary>

    <div v-if="coverage && (missing !== null || flaggedShown > 0)" class="shrink-0 space-y-1.5 text-2xs text-[var(--c-text-muted)]">
      <p v-if="missing !== null">
        <template v-if="missing.length">
          <strong>{{ formatFull(missing.length) }} {{ missing.length === 1 ? 'day is' : 'days are' }} missing</strong>
          from the source and appear as gaps rather than interpolated points: {{ missing.join(', ') }}.
        </template>
        <template v-else>Every expected day in this range was delivered and imported.</template>
      </p>
      <p v-if="flaggedShown > 0">
        <strong>{{ formatFull(flaggedShown) }} shaded {{ flaggedShown === 1 ? 'day is one' : 'days are ones' }}</strong>
        whose file looked unlike the ordinary days - IMEIs shifted by a digit, or SIMs carrying several numbers. Their
        figures may be distorted; hover a day for what was found.
      </p>
    </div>
  </div>
</template>
