<script setup lang="ts">
import { computed } from 'vue'
import { useQualitySignals, type QualityGroup, type QualitySignal } from '@/api/quality'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull, formatPercent } from '@/lib/format'
import type { SignalsConfig } from './config'
import { useKept } from '../overview/shared/useKept'

/**
 * The data-quality categories of the newest published measures run: identifier faults in current
 * state, sequences the feed's own history contradicts, and how long the feed held a binding. Facts
 * about the feed - nothing here is evidence about a subscriber.
 */
const props = defineProps<WidgetViewProps<SignalsConfig>>()

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const query = useQualitySignals({ enabled: active })
const signals = useWidgetData(props, query, (raw) => (raw as SnapshotData<'quality.signals'>).signals)
const { value: data, loading, updating } = useKept(signals)

const GROUP_TITLES: Record<QualityGroup, string> = {
  identifiers: 'Identifiers - current state',
  sequences: 'Sequences the history contradicts',
  lifetimes: 'How long the feed held a binding',
}

const UNIT_WORDS = { bindings: 'Bindings', numbers: 'Numbers', sims: 'SIMs, est.', imeis: 'IMEIs, est.' } as const

const groups = computed(() =>
  config.value.groups.map((g) => ({ group: g, rows: (data.value?.categories ?? []).filter((c) => c.group === g) })).filter((g) => g.rows.length > 0),
)

function value(c: QualitySignal): number {
  return c[config.value.unit]
}

function share(c: QualitySignal): string {
  return c.share === null ? '—' : formatPercent(c.share * 100, c.share < 0.001 ? 3 : 2)
}

const bars = computed(() =>
  groups.value.flatMap((g) => g.rows).map((c) => ({ key: c.label, count: value(c), percent: (c.share ?? 0) * 100 })),
)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Facts about the feed and its identifiers, counted with each measures run<template v-if="data?.available && data.asOf">
        - data through {{ formatDate(data.asOf) }}</template>. Nothing here is evidence about a subscriber.
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="signals.isError.value"
      :error="signals.error.value"
      min-height="8rem"
      gap="none"
      @retry="signals.refetch()"
    >
      <p
        v-if="data && !data.available"
        class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-3 py-2 text-xs text-[var(--c-text-secondary)]"
        role="status"
      >
        {{ data.reason }}
      </p>
      <div v-else-if="config.display === 'bar'" class="relative min-h-0 flex-1">
        <BarChart :data="bars" :unit="UNIT_WORDS[config.unit].toLowerCase()" height="100%" :selectable="false" />
      </div>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 z-[1] bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">Category</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">{{ UNIT_WORDS[config.unit] }}</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Share of bindings</th>
            </tr>
          </thead>
          <template v-for="g in groups" :key="g.group">
            <tbody>
              <tr class="border-t bg-[var(--c-surface-sunken)]">
                <th scope="rowgroup" colspan="3" class="px-2 py-1 text-left text-2xs font-semibold text-[var(--c-text-secondary)]">{{ GROUP_TITLES[g.group] }}</th>
              </tr>
              <tr v-for="c in g.rows" :key="c.code" class="border-t align-top">
                <th scope="row" class="px-2 py-1.5 text-left font-normal">
                  <span class="font-medium">{{ c.label }}</span>
                  <span class="block text-2xs text-[var(--c-text-muted)]">{{ c.meaning }}</span>
                </th>
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(value(c)) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ share(c) }}</td>
              </tr>
            </tbody>
          </template>
        </table>
      </div>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      A binding can be in more than one category. SIMs and IMEIs are estimated (about 0.5%); bindings and numbers are exact.
      Shares are of the category's base: all bindings for identifiers, the history for sequences and periods.
    </p>
  </div>
</template>
