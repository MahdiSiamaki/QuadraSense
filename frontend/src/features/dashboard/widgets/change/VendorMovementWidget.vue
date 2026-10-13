<script setup lang="ts">
import { computed, inject } from 'vue'
import { useVendorMovement, type VendorRanking } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import VendorMovementChart from '@/design-system/VendorMovementChart.vue'
import { resolveRange } from '@/features/dashboard/composables/lens'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import { DASHBOARD_CONTEXT, type WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { VendorMovementConfig } from './config'
import { useKept } from '../overview/shared/useKept'

/**
 * Vendors measured three ways - the old Vendors card, with its mode, limit and normalisation now in
 * the widget's config and its date range taken from the dashboard.
 *
 * - **Movement** counts add and remove *events* in the range. A SIM moved between two handsets thirty
 *   times contributes thirty of each and changes the population by nothing.
 * - **Share** is the population now.
 * - **Growth** compares the population against the first delivery, relative to the network.
 *
 * Only movement reads the date range; share and growth are fixed points (the first delivery and the
 * latest), so the range is not sent for them and the card does not claim it applies.
 */
const props = defineProps<WidgetViewProps<VendorMovementConfig>>()

const HELP: Record<VendorRanking, string> = {
  movement:
    'Add and remove events in the window. Counts events, not devices — a SIM that moves between handsets repeatedly appears many times and changes the population by nothing.',
  share: 'Active bindings now, as a share of the whole network. The figure to use for "how big is this vendor".',
  growth:
    'Population now against the first delivery, relative to the network. Shown in points of share gained or lost, because every absolute change is negative.',
}

const config = computed(() => props.instance.config)
const active = useQueryActive(props)
const context = inject(DASHBOARD_CONTEXT, null)
const window = computed(() =>
  config.value.rank === 'movement' ? resolveRange(props.lens.range, context?.dataThrough.value ?? null) : { from: null, to: null },
)

const filters = computed(() => ({
  rank: config.value.rank,
  from: window.value.from,
  to: window.value.to,
  limit: config.value.limit,
}))
const query = useVendorMovement(filters, { enabled: active })
const movement = useWidgetData(props, query, (raw) => (raw as SnapshotData<'change.vendorMovement'>).movement)
const { value: data, loading, updating } = useKept(movement)

/** Normalising share or growth is meaningless - both are already relative. */
const normalised = computed(() => config.value.rank === 'movement' && config.value.normalised)

const windowLabel = computed(() => {
  const d = data.value
  if (!d) return ''
  if (!d.from && !d.to) {
    return d.earliestAvailable && d.latestAvailable
      ? `all of ${formatDate(d.earliestAvailable)} – ${formatDate(d.latestAvailable)}`
      : 'all available days'
  }
  return `${d.from ? formatDate(d.from) : '…'} – ${d.to ? formatDate(d.to) : '…'}`
})

function signed(value: number | null, digits = 0, suffix = ''): string {
  if (value === null) return '—'
  const text = digits ? Math.abs(value).toFixed(digits) : formatFull(Math.abs(value))
  return `${value > 0 ? '+' : value < 0 ? '−' : ''}${text}${suffix}`
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">{{ HELP[config.rank] }}</p>

    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="movement.isError.value"
      :error="movement.error.value"
      :is-empty="!loading && (data?.rows.length ?? 0) === 0"
      min-height="10rem"
      gap="none"
      @retry="movement.refetch()"
    >
      <div v-if="data" class="min-h-0 flex-1 overflow-y-auto overscroll-contain">
        <VendorMovementChart
          v-if="config.display === 'chart'"
          :rows="data.rows"
          :ranking="config.rank"
          :normalised="normalised"
          :network-change-percent="data.networkChangePercent"
        />
        <table v-else class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr v-if="config.rank === 'movement'">
              <th scope="col" class="px-2 py-1.5 font-medium">Vendor</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Adds</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Removes</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">{{ normalised ? 'Net, % of population' : 'Net events' }}</th>
            </tr>
            <tr v-else-if="config.rank === 'share'">
              <th scope="col" class="px-2 py-1.5 font-medium">Vendor</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Active bindings</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Share</th>
            </tr>
            <tr v-else>
              <th scope="col" class="px-2 py-1.5 font-medium">Vendor</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Change</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">vs network</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in data.rows" :key="r.vendor" class="border-t">
              <td class="px-2 py-1.5 [overflow-wrap:anywhere]">{{ r.vendor }}</td>
              <template v-if="config.rank === 'movement'">
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(r.added) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(r.removed) }}</td>
                <td class="tabular px-2 py-1.5 text-right">
                  {{ normalised ? signed(r.netPercentOfPopulation, 2, '%') : signed(r.net) }}
                </td>
              </template>
              <template v-else-if="config.rank === 'share'">
                <td class="tabular px-2 py-1.5 text-right">{{ formatFull(r.population) }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ r.sharePercent.toFixed(2) }}%</td>
              </template>
              <template v-else>
                <td class="tabular px-2 py-1.5 text-right">{{ signed(r.populationChangePercent, 2, '%') }}</td>
                <td class="tabular px-2 py-1.5 text-right">{{ signed(r.vsNetworkPoints, 2, ' pts') }}</td>
              </template>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>

    <div v-if="data" class="shrink-0 border-t pt-2 text-2xs text-[var(--c-text-muted)]">
      <p v-if="config.rank === 'growth'">
        Network: <span class="tabular">{{ formatFull(data.networkPopulationAtStart) }}</span> →
        <span class="tabular">{{ formatFull(data.networkPopulation) }}</span>
        (<span class="tabular">{{ data.networkChangePercent.toFixed(2) }}%</span>). Bars show each vendor's change
        <em>against</em> that, so positive means share gained even where the count fell.
        <span v-if="data.startIsInitialDump" class="text-[var(--c-warning-text)]">
          The starting point is the initial dump, which covers a 30-day window rather than an instant and averages 1.57
          handsets per SIM — so part of the network's decline is that over-count being resolved, not devices leaving.
        </span>
      </p>
      <p v-else-if="config.rank === 'share'">
        <span class="tabular">{{ formatFull(data.networkPopulation) }}</span> active bindings across the network. A
        binding is one number + SIM + handset, so a subscriber with two handsets counts twice.
      </p>
      <p v-else>
        Net events over {{ windowLabel }}.
        <template v-if="normalised">
          Shown as a percentage of each vendor's own population, so a small vendor with a big swing is comparable to a
          large one.
        </template>
        <template v-else>
          These are <em>events</em>, not devices — one SIM moving between handsets thirty times contributes thirty adds
          and thirty removes and changes the population by nothing. Switch to Growth for the population figure.
        </template>
      </p>
    </div>
  </div>
</template>
