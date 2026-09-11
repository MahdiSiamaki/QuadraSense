<script setup lang="ts">
import { computed, ref } from 'vue'
import { useKpiSummary, useTopDimension, useDistribution, type DashboardFilters } from '@/api/dashboard'
import Card from '@/design-system/Card.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import { formatFull, formatPercent } from '@/lib/format'

/*
  Filters live in component state for now. Phase 4 moves them into the URL so a
  view is shareable and reloadable - the shape here is already the shape that
  will be serialised, so that move is mechanical rather than a rewrite.
*/
const filters = ref<DashboardFilters>({ includeUnknownDevice: true })

const kpi = useKpiSummary(filters)
const vendors = useTopDimension('vendorCanonical', filters, 10)
const deviceTypes = useDistribution('deviceType', filters)
const operatingSystems = useTopDimension('operatingSystem', filters, 8)

/** Clicking a bar narrows the dashboard - the drill-down the brief asks for. */
function drillIntoVendor(key: string) {
  if (key.startsWith('(unknown')) return
  filters.value = { ...filters.value, vendor: key }
}

function clearFilters() {
  filters.value = { includeUnknownDevice: true }
}

const hasFilters = computed(() =>
  Boolean(filters.value.vendor || filters.value.deviceType || filters.value.operatingSystem),
)

const unknownShare = computed(() => {
  const k = kpi.data.value
  if (!k || !k.activeBindings) return null
  return (k.unknownDeviceBindings / k.activeBindings) * 100
})
</script>

<template>
  <div class="space-y-5">
    <!-- Page header -->
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Device population</h1>
        <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
          Active device–SIM bindings across the network, enriched with GSMA device data.
        </p>
      </div>

      <div v-if="hasFilters" class="flex items-center gap-2">
        <span
          v-if="filters.vendor"
          class="inline-flex items-center gap-1.5 rounded-full bg-[var(--c-accent-subtle)] px-2.5 py-1 text-[var(--text-xs)] font-medium"
        >
          {{ filters.vendor }}
        </span>
        <button
          type="button"
          class="rounded-[var(--radius-sm)] border px-2.5 py-1 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          @click="clearFilters"
        >
          Clear
        </button>
      </div>
    </header>

    <!-- KPI row -->
    <AsyncBoundary
      :is-loading="kpi.isPending.value"
      :is-error="kpi.isError.value"
      :error="kpi.error.value"
      min-height="5.5rem"
      @retry="kpi.refetch()"
    >
      <template #skeleton>
        <div class="grid grid-cols-2 gap-3 lg:grid-cols-5">
          <div v-for="i in 5" :key="i" class="h-[5.5rem] rounded-[var(--radius-lg)] bg-[var(--c-surface-sunken)]" />
        </div>
      </template>

      <div class="grid grid-cols-2 gap-3 lg:grid-cols-5">
        <KpiCard label="Active bindings" :value="kpi.data.value?.activeBindings" />
        <KpiCard label="Subscribers" :value="kpi.data.value?.distinctSubscribers" hint="approx." />
        <KpiCard label="Devices" :value="kpi.data.value?.distinctDevices" hint="approx." />
        <KpiCard
          label="Unknown device"
          :value="kpi.data.value?.unknownDeviceBindings"
          tone="warning"
          :hint="unknownShare ? `${formatPercent(unknownShare)} of bindings` : undefined"
        />
        <KpiCard
          label="TAC coverage"
          :value="kpi.data.value?.tacCoveragePercent"
          percent
          hint="enriched from GSMA"
        />
      </div>
    </AsyncBoundary>

    <!-- Charts -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card title="Top vendors" subtitle="By active bindings. Click a bar to filter.">
        <AsyncBoundary
          :is-loading="vendors.isPending.value"
          :is-error="vendors.isError.value"
          :error="vendors.error.value"
          :is-empty="vendors.data.value?.length === 0"
          min-height="18rem"
          @retry="vendors.refetch()"
        >
          <BarChart :data="vendors.data.value ?? []" @select="drillIntoVendor" />
        </AsyncBoundary>
      </Card>

      <Card title="Device types" subtitle="Smartphone, feature phone, modem and IoT mix.">
        <AsyncBoundary
          :is-loading="deviceTypes.isPending.value"
          :is-error="deviceTypes.isError.value"
          :error="deviceTypes.error.value"
          :is-empty="deviceTypes.data.value?.length === 0"
          min-height="18rem"
          @retry="deviceTypes.refetch()"
        >
          <BarChart :data="(deviceTypes.data.value ?? []).slice(0, 10)" />
        </AsyncBoundary>
      </Card>
    </div>

    <Card title="Operating systems" subtitle="Normalised for case and whitespace.">
      <AsyncBoundary
        :is-loading="operatingSystems.isPending.value"
        :is-error="operatingSystems.isError.value"
        :error="operatingSystems.error.value"
        :is-empty="operatingSystems.data.value?.length === 0"
        min-height="15rem"
        @retry="operatingSystems.refetch()"
      >
        <table class="w-full text-[var(--text-sm)]">
          <thead>
            <tr class="border-b text-left text-[var(--text-xs)] text-[var(--c-text-muted)]">
              <th class="pb-2 font-medium">Operating system</th>
              <th class="pb-2 text-right font-medium">Bindings</th>
              <th class="pb-2 text-right font-medium">Share</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="row in operatingSystems.data.value ?? []"
              :key="row.key"
              class="border-b last:border-0 hover:bg-[var(--c-surface-hover)]"
            >
              <td class="py-2 pr-4">{{ row.key }}</td>
              <td class="py-2 text-right tabular">{{ formatFull(row.count) }}</td>
              <td class="py-2 text-right tabular text-[var(--c-text-secondary)]">
                {{ formatPercent(row.percent, 2) }}
              </td>
            </tr>
          </tbody>
        </table>
      </AsyncBoundary>
    </Card>
  </div>
</template>
