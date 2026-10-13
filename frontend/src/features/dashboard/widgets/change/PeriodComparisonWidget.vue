<script setup lang="ts">
import { computed, inject } from 'vue'
import { useDailyChanges, useDailyChurn } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull, formatSignedDecimal } from '@/lib/format'
import type { ComparisonMetric, PeriodComparisonConfig } from './config'
import { comparePeriods, usesChurn } from './periodComparison'
import { lastDate } from '../overview/shared/series'
import { useKept } from '../overview/shared/useKept'

/**
 * One daily figure for the dashboard's range against a baseline - the period before it, or dates the
 * widget names. Network-wide, from the daily series (`periodComparison.ts` has the arithmetic).
 *
 * The change is shown in neutral ink, never green or red: more removals is not good news, and more
 * adds is not bad, so a colour would assert a judgement the figure does not carry.
 */
const props = defineProps<WidgetViewProps<PeriodComparisonConfig>>()
type Snapshot = SnapshotData<'change.periodComparison'>

interface MetricSpec {
  label: string
  unit: string
}

const METRICS: Record<ComparisonMetric, MetricSpec> = {
  added: { label: 'Bindings added', unit: 'add events' },
  removed: { label: 'Bindings removed', unit: 'remove events' },
  net: { label: 'Net change', unit: 'bindings, added minus removed' },
  simChanges: { label: 'SIM changes', unit: 'numbers that moved to another SIM (same-day definition)' },
  deviceChanges: { label: 'Handset changes', unit: 'numbers that moved to another handset (same-day definition)' },
  population: { label: 'Active bindings', unit: 'bindings - number + SIM + handset' },
}

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const churnMetric = computed(() => usesChurn(config.value.metric))

const changesQuery = useDailyChanges({ enabled: () => active.value && !churnMetric.value })
const churnQuery = useDailyChurn({ enabled: () => active.value && churnMetric.value })
const changes = useWidgetData(props, changesQuery, (raw) => (raw as Snapshot).changes ?? undefined)
const churn = useWidgetData(props, churnQuery, (raw) => (raw as Snapshot).churn ?? undefined)
const source = computed(() => (churnMetric.value ? churn : changes))

const keptChanges = useKept(changes)
const keptChurn = useKept(churn)
const kept = computed(() => (churnMetric.value ? keptChurn : keptChanges))
const rows = computed(() => (kept.value.value.value ?? []) as Array<{ date: string }>)

const context = inject(DASHBOARD_CONTEXT, null)
const dataStart = computed(() => rows.value.reduce<string | null>((first, r) => (first === null || r.date < first ? r.date : first), null))
const dataThrough = computed(() => context?.dataThrough.value ?? lastDate(rows.value))

const current = computed(() => {
  if (dataStart.value === null || dataThrough.value === null) return null
  const r = resolveRange(props.lens.range, dataThrough.value)
  return { from: r.from ?? dataStart.value, to: r.to ?? dataThrough.value }
})

const result = computed(() =>
  current.value === null
    ? null
    : comparePeriods({
        config: config.value,
        current: current.value,
        rows: kept.value.value.value ?? [],
        dataStart: dataStart.value,
      }),
)

const metric = computed(() => METRICS[config.value.metric])
const average = computed(() => config.value.aggregate === 'dailyAverage')
const digits = computed(() => (average.value ? 1 : 0))

function figure(value: number | null): string {
  if (value === null) return '—'
  return average.value ? formatSignedDecimal(value, 1).replace(/^\+/, '') : formatFull(value)
}

function period(p: { from: string; to: string; deliveredDays: number; calendarDays: number }): string {
  const days = p.deliveredDays === p.calendarDays ? `${p.calendarDays} days` : `${p.deliveredDays} of ${p.calendarDays} days delivered`
  return `${formatDate(p.from)} – ${formatDate(p.to)} · ${days}`
}

const valueLabel = computed(() => {
  if (config.value.metric === 'population') return average.value ? 'Average over the delivered days' : 'On the last delivered day'
  return average.value ? 'Per delivered day, on average' : 'Total over the delivered days'
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="kept.updating.value">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      {{ metric.label }}, {{ valueLabel.toLowerCase() }}: the dashboard's range against
      {{ config.baseline === 'previous' ? 'the same number of days before it' : 'a fixed baseline' }}. Network-wide.
    </p>

    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="kept.loading.value"
      :is-error="source.isError.value"
      :error="source.error.value"
      :is-empty="!kept.loading.value && rows.length === 0"
      empty-message="No daily files loaded yet."
      min-height="6rem"
      gap="none"
      @retry="source.refetch()"
    >
      <div v-if="result" class="flex min-h-0 flex-1 flex-col gap-3 overflow-auto">
        <div>
          <p class="text-2xs text-[var(--c-text-muted)]">{{ period(result.current) }}</p>
          <p class="tabular text-2xl font-semibold tracking-tight">{{ figure(result.current.value) }}</p>
          <p class="text-2xs text-[var(--c-text-muted)]">{{ metric.unit }}</p>
        </div>

        <p v-if="result.unavailable" class="text-xs text-[var(--c-text-secondary)]" role="status">{{ result.unavailable }}</p>

        <dl v-else-if="result.baseline" class="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-1 text-xs">
          <dt class="text-[var(--c-text-muted)]">Baseline</dt>
          <dd class="tabular">
            {{ figure(result.baseline.value) }}
            <span class="block text-2xs text-[var(--c-text-muted)]">{{ period(result.baseline) }}</span>
          </dd>
          <template v-if="config.change !== 'percent'">
            <dt class="text-[var(--c-text-muted)]">Change</dt>
            <dd class="tabular font-medium">{{ result.absolute === null ? '—' : formatSignedDecimal(result.absolute, digits) }}</dd>
          </template>
          <template v-if="config.change !== 'absolute'">
            <dt class="text-[var(--c-text-muted)]">Change, %</dt>
            <dd class="tabular font-medium">
              <template v-if="result.percent !== null">{{ formatSignedDecimal(result.percent, 1) }}%</template>
              <span v-else class="font-normal text-[var(--c-text-muted)]">none - the baseline is zero</span>
            </dd>
          </template>
        </dl>
      </div>
    </AsyncBoundary>

    <p v-if="result?.uneven" class="shrink-0 text-2xs text-[var(--c-warning-text)]">
      The two periods have different numbers of delivered days, so their totals are not like for like. A missing day is
      never filled in; switch the widget to a daily average to compare them fairly.
    </p>
  </div>
</template>
