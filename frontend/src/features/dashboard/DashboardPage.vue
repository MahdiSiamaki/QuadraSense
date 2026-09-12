<script setup lang="ts">
import { computed } from 'vue'
import {
  useKpiSummary,
  useTopDimension,
  useDistribution,
  useDeviceClassMix,
  useCapabilities,
} from '@/api/dashboard'
import Card from '@/design-system/Card.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import CompositionBar from '@/design-system/CompositionBar.vue'
import CapabilityBars from '@/design-system/CapabilityBars.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { formatFull, formatPercent } from '@/lib/format'
import { useFilterState, FILTER_LABELS } from './useFilterState'

const { filters, activeFilters, setFilter, toggleUnknownDevice, clearAll } = useFilterState()

const kpi = useKpiSummary(filters)
const vendors = useTopDimension('vendorCanonical', filters, 10)
const deviceTypes = useDistribution('deviceType', filters)
const operatingSystems = useTopDimension('operatingSystem', filters, 8)
const classMix = useDeviceClassMix()
const capabilities = useCapabilities()

/**
 * Top models always excludes the unknown-device bucket.
 *
 * Not a cosmetic choice. "(unknown device)" is 8.8M bindings — larger than every
 * real model — so including it dwarfs the bars and makes the models
 * incomparable, which is the one thing this chart exists to do. And it is not an
 * answer to "which models are popular": it is the absence of one.
 *
 * The count is not hidden: it stays in the KPI row, the class mix, and the
 * device-type table, where it genuinely is a category.
 */
const modelFilters = computed(() => ({ ...filters.value, includeUnknownDevice: false }))
const models = useTopDimension('marketingName', modelFilters, 10)

/** Clicking a bar drills in. The URL changes, so back undoes it. */
function drillInto(key: keyof typeof FILTER_LABELS, value: string) {
  if (value.startsWith('(unknown')) return
  setFilter(key, value)
}

const unknownShare = computed(() => {
  const k = kpi.data.value
  if (!k?.activeBindings) return null
  return (k.unknownDeviceBindings / k.activeBindings) * 100
})

/**
 * Bindings with a well-formed IMEI whose TAC is absent from the GSMA database.
 *
 * Computed from exact counts the API returns, never from the coverage percentage.
 * Deriving it from a figure rounded to three decimals put this ~30,000 rows off
 * the true 255,676 - a rounding artifact presented as a count, on the screen
 * whose whole job is to report data quality accurately.
 */
const unregisteredTac = computed(() => {
  const k = kpi.data.value
  if (!k?.activeBindings) return null
  return (
    k.activeBindings - k.tacMatchedBindings - k.unknownDeviceBindings - k.malformedImeiBindings
  )
})

/** The filtered path falls back to the raw table and is measurably slower. */
const isFiltered = computed(() => activeFilters.value.length > 0)
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-[var(--text-xl)] font-semibold tracking-tight">Device population</h1>
        <p class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-muted)]">
          Active device–SIM bindings across the network, enriched with GSMA device data.
        </p>
        <p class="mt-1 text-[var(--text-2xs)] text-[var(--c-text-muted)]">
          A <strong>binding</strong> is one number + SIM + handset combination
          (<code class="font-[var(--font-mono)]">MSISDN</code> +
          <code class="font-[var(--font-mono)]">IMSI</code> +
          <code class="font-[var(--font-mono)]">IMEI</code>). One subscriber can hold several.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          class="rounded-[var(--radius-md)] border px-2.5 py-1.5 text-[var(--text-xs)] font-medium hover:bg-[var(--c-surface-hover)]"
          :class="filters.includeUnknownDevice ? '' : 'bg-[var(--c-surface-sunken)]'"
          @click="toggleUnknownDevice"
        >
          {{ filters.includeUnknownDevice ? 'Hide unknown devices' : 'Show unknown devices' }}
        </button>
      </div>
    </header>

    <!-- Active filters. Shown as removable chips so the current view is always legible. -->
    <div v-if="isFiltered" class="flex flex-wrap items-center gap-2">
      <span class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Filtered by</span>
      <button
        v-for="f in activeFilters"
        :key="f.key"
        type="button"
        class="group inline-flex items-center gap-1.5 rounded-full bg-[var(--c-accent-subtle)] py-1 pr-2 pl-2.5 text-[var(--text-xs)] font-medium"
        :title="`Remove ${FILTER_LABELS[f.key]} filter`"
        @click="setFilter(f.key, undefined)"
      >
        <span class="text-[var(--c-text-secondary)]">{{ FILTER_LABELS[f.key] }}:</span>
        <span>{{ f.value }}</span>
        <span aria-hidden="true" class="text-[var(--c-text-muted)] group-hover:text-[var(--c-text)]">×</span>
      </button>
      <button
        type="button"
        class="rounded-[var(--radius-sm)] px-2 py-1 text-[var(--text-xs)] font-medium text-[var(--c-text-secondary)] underline-offset-2 hover:underline"
        @click="clearAll"
      >
        Clear all
      </button>
      <span class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
        · filtered views query raw data and take a few seconds
      </span>
    </div>

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

      <div class="grid grid-cols-2 gap-3 lg:grid-cols-3 xl:grid-cols-6">
        <KpiCard
          label="Active bindings"
          :value="kpi.data.value?.activeBindings"
          identifier="MSISDN + IMSI + IMEI"
          definition="Rows in the current state: one per distinct number-SIM-handset combination. This is the grain of the whole dataset, not a count of people."
        />
        <KpiCard
          label="Subscribers"
          :value="kpi.data.value?.distinctSubscribers"
          identifier="MSISDN"
          qualifier="approx."
          definition="Distinct phone numbers with at least one active binding. Estimated with HyperLogLog (~0.5% error); exact counts are available via export."
        />
        <KpiCard
          label="SIM cards"
          :value="kpi.data.value?.distinctSims"
          identifier="IMSI"
          qualifier="approx."
          definition="Distinct SIM identities. Slightly higher than the number of subscribers because a number that has been swapped to a new SIM has more than one IMSI."
        />
        <KpiCard
          label="Handsets"
          :value="kpi.data.value?.distinctDevices"
          identifier="IMEI"
          qualifier="approx."
          definition="Distinct 14-digit IMEIs. Excludes the 000000 unknown-device sentinel, which is one literal value shared by 8.8M bindings and is not a handset."
        />
        <KpiCard
          label="Unknown device"
          :value="kpi.data.value?.unknownDeviceBindings"
          tone="warning"
          identifier="IMEI = 000000"
          :qualifier="unknownShare ? `${formatPercent(unknownShare)} of bindings` : undefined"
          definition="Bindings where the source reports the handset as unknown. A sentinel value, not a data defect - these are real subscribers whose device the operator did not record."
        />
        <KpiCard
          label="TAC coverage"
          :value="kpi.data.value?.tacCoveragePercent"
          percent
          identifier="TAC"
          qualifier="of bindings"
          definition="Share of active bindings whose TAC (the first 8 digits of the IMEI) matched the GSMA device database, so manufacturer and model are known."
        />
      </div>
    </AsyncBoundary>

    <!-- Composition -->
    <Card
      title="Device class mix"
      subtitle="Handsets versus machines. IoT/M2M is a real segment here, not tail noise."
    >
      <AsyncBoundary
        :is-loading="classMix.isPending.value"
        :is-error="classMix.isError.value"
        :error="classMix.error.value"
        min-height="6rem"
        @retry="classMix.refetch()"
      >
        <CompositionBar :data="classMix.data.value ?? []" />
      </AsyncBoundary>
    </Card>

    <!-- Network and SIM capability -->
    <Card
      title="Network & SIM capability"
      subtitle="What the active device base supports, from the GSMA band list and eUICC records."
    >
      <AsyncBoundary
        :is-loading="capabilities.isPending.value"
        :is-error="capabilities.isError.value"
        :error="capabilities.error.value"
        min-height="12rem"
        @retry="capabilities.refetch()"
      >
        <CapabilityBars :data="capabilities.data.value ?? []" />
      </AsyncBoundary>

      <template #footer>
        <p class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
          <strong>VoLTE is not shown</strong> because the GSMA dataset does not contain it: the band
          list mentions VoLTE in 2 of 270,166 records, and the IMS fields describe emergency calling
          rather than VoLTE. A proxy would look like an answer without being one.
        </p>
      </template>
    </Card>

    <!-- Vendors and models -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card title="Top vendors" subtitle="By active bindings, not handsets. Click a bar to filter.">
        <AsyncBoundary
          :is-loading="vendors.isPending.value"
          :is-error="vendors.isError.value"
          :error="vendors.error.value"
          :is-empty="vendors.data.value?.length === 0"
          min-height="18rem"
          @retry="vendors.refetch()"
        >
          <BarChart :data="vendors.data.value ?? []" @select="(v) => drillInto('vendor', v)" />
        </AsyncBoundary>

        <template #footer>
          <p class="text-[var(--text-2xs)] text-[var(--c-text-muted)]">
            These are <strong>bindings</strong>, not handsets. A dual-SIM phone serving two numbers
            counts twice. For Samsung that is 52.7M bindings against 39.3M distinct handsets and
            40.1M subscribers &mdash; so reading this as a handset count overstates it by about a third.
          </p>
        </template>
      </Card>

      <Card title="Top models" subtitle="By active bindings. Excludes unknown devices.">
        <AsyncBoundary
          :is-loading="models.isPending.value"
          :is-error="models.isError.value"
          :error="models.error.value"
          :is-empty="models.data.value?.length === 0"
          min-height="18rem"
          @retry="models.refetch()"
        >
          <BarChart :data="models.data.value ?? []" />
        </AsyncBoundary>
      </Card>
    </div>

    <!-- Types and OS -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card title="Device types" subtitle="By active bindings. Click to filter." flush>
        <AsyncBoundary
          :is-loading="deviceTypes.isPending.value"
          :is-error="deviceTypes.isError.value"
          :error="deviceTypes.error.value"
          :is-empty="deviceTypes.data.value?.length === 0"
          min-height="16rem"
          @retry="deviceTypes.refetch()"
        >
          <DimensionTable
            :rows="(deviceTypes.data.value ?? []).slice(0, 10)"
            header="Device type"
            @select="(v) => drillInto('deviceType', v)"
          />
        </AsyncBoundary>
      </Card>

      <Card title="Operating systems" subtitle="By active bindings. Normalised for case and whitespace." flush>
        <AsyncBoundary
          :is-loading="operatingSystems.isPending.value"
          :is-error="operatingSystems.isError.value"
          :error="operatingSystems.error.value"
          :is-empty="operatingSystems.data.value?.length === 0"
          min-height="16rem"
          @retry="operatingSystems.refetch()"
        >
          <DimensionTable
            :rows="operatingSystems.data.value ?? []"
            header="Operating system"
            @select="(v) => drillInto('operatingSystem', v)"
          />
        </AsyncBoundary>
      </Card>
    </div>

    <!-- Data quality -->
    <Card
      title="Enrichment breakdown"
      subtitle="Every active binding, accounted for. These are characteristics of the feed, not defects — except where noted."
    >
      <AsyncBoundary
        :is-loading="kpi.isPending.value"
        :is-error="kpi.isError.value"
        :error="kpi.error.value"
        min-height="8rem"
        @retry="kpi.refetch()"
      >
        <div class="space-y-4">
          <!-- The four categories are shown together, and they sum exactly to the total.
               Presenting them separately is the point: only one of them is a defect. -->
          <dl class="grid gap-x-8 gap-y-4 sm:grid-cols-2 lg:grid-cols-4">
            <div>
              <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Enriched from GSMA</dt>
              <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-success)]">
                {{ formatFull(kpi.data.value?.tacMatchedBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                {{ formatPercent(kpi.data.value?.tacCoveragePercent ?? 0, 2) }} of active bindings.
              </dd>
            </div>

            <div>
              <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">
                Unknown device (<code class="font-[var(--font-mono)]">000000</code>)
              </dt>
              <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-warning)]">
                {{ formatFull(kpi.data.value?.unknownDeviceBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                A sentinel the source uses for "device not known". Expected, not a defect.
              </dd>
            </div>

            <div>
              <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Malformed IMEI</dt>
              <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular text-[var(--c-danger)]">
                {{ formatFull(kpi.data.value?.malformedImeiBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                Numeric but not 14 digits. <strong>The only genuine defect here</strong> — worth raising
                with the source.
              </dd>
            </div>

            <div>
              <dt class="text-[var(--text-xs)] text-[var(--c-text-muted)]">Unregistered TAC</dt>
              <dd class="mt-1 text-[var(--text-lg)] font-semibold tabular">
                {{ formatFull(unregisteredTac ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-[var(--text-xs)] text-[var(--c-text-secondary)]">
                Well-formed IMEI whose TAC is absent from the GSMA database.
              </dd>
            </div>
          </dl>

          <p class="border-t pt-3 text-[var(--text-xs)] text-[var(--c-text-muted)]">
            The four figures sum to
            <span class="tabular font-medium text-[var(--c-text-secondary)]">
              {{ formatFull(kpi.data.value?.activeBindings ?? 0) }}
            </span>
            active bindings. The enrichment ceiling is set by unknown-device rows, not by gaps in the
            GSMA database.
          </p>
        </div>
      </AsyncBoundary>
    </Card>
  </div>
</template>
