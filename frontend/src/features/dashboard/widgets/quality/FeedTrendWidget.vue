<script setup lang="ts">
import { computed, inject } from 'vue'
import { CHECK_LABELS, useFeedQuality } from '@/api/quality'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull, formatPercent } from '@/lib/format'
import type { FeedTrendConfig } from './config'
import FeedTrendChart from './FeedTrendChart.vue'
import { checkCount, checkRate } from './feedTrend'
import { cutToWindow, lastDate } from '../overview/shared/series'
import { useKept } from '../overview/shared/useKept'

/**
 * Each daily file's feed-quality checks over the dashboard's range - shifted IMEIs, SIMs with several
 * numbers, malformed IMEIs, unknown-device rows - as rates against the monitor's flagging line, or as
 * row counts. The data-quality page's day-by-day judgement, drawn over time.
 */
const props = defineProps<WidgetViewProps<FeedTrendConfig>>()

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const query = useFeedQuality({ enabled: active })
const feed = useWidgetData(props, query, (raw) => (raw as SnapshotData<'quality.feedTrend'>).feedQuality)
const { value: data, loading, updating } = useKept(feed)

const context = inject(DASHBOARD_CONTEXT, null)
const all = computed(() => data.value?.days ?? [])
const window = computed(() => resolveRange(props.lens.range, context?.dataThrough.value ?? lastDate(all.value)))
const shown = computed(() => cutToWindow(all.value, window.value))
const whole = computed(() => window.value.from === null && window.value.to === null)
const flaggedShown = computed(() => shown.value.filter((d) => d.flagged).length)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Each daily file judged against the ordinary days<template v-if="data">
        ({{ formatDate(data.reference.from) }} – {{ formatDate(data.reference.to) }}): a day is flagged when a rate is
        above {{ data.reference.multiplier }}× their median</template>.
      <template v-if="config.measure === 'count'"> Counted in rows, including SIMs with several numbers.</template>
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="feed.isError.value"
      :error="feed.error.value"
      :is-empty="!loading && shown.length === 0"
      :empty-message="all.length === 0 ? 'No day has been measured yet.' : 'No measured day in this date range.'"
      min-height="10rem"
      gap="none"
      @retry="feed.refetch()"
    >
      <div v-if="config.display === 'chart'" class="relative min-h-0 flex-1">
        <FeedTrendChart
          :days="shown"
          :response="data"
          :checks="config.checks"
          :measure="config.measure"
          :show-reference="config.showReference"
          :zoom-start="whole ? 50 : 0"
          height="100%"
        />
      </div>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">Day</th>
              <th v-for="c in config.checks" :key="c" scope="col" class="px-2 py-1.5 text-right font-medium">{{ CHECK_LABELS[c] }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="d in [...shown].reverse()" :key="d.date" class="border-t" :class="d.flagged ? 'bg-[var(--c-warning-subtle)]' : ''">
              <td class="tabular px-2 py-1.5 whitespace-nowrap">{{ formatDate(d.date) }}<span v-if="d.flagged" class="sr-only"> (flagged)</span></td>
              <td v-for="c in config.checks" :key="c" class="tabular px-2 py-1.5 text-right">
                <template v-if="config.measure === 'rate'">{{ checkRate(d, c) === null ? '—' : formatPercent(checkRate(d, c)! * 100, 2) }}</template>
                <template v-else>{{ formatFull(checkCount(d, c)) }}</template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>
    <p v-if="shown.length" class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      {{ formatFull(flaggedShown) }} of {{ formatFull(shown.length) }} measured days in this range were out of line<template
        v-if="config.display === 'chart'"> (shaded)</template><template v-if="config.display === 'chart' && config.measure === 'rate' && config.showReference">; the dashed lines are where each check flags a day</template>.
    </p>
  </div>
</template>
