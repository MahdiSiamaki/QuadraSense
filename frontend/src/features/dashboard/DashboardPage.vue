<script setup lang="ts">
import { computed } from 'vue'
import { useAuth, Permission } from '@/features/auth/useAuth'
import {
  useKpiSummary,
  useTopDimension,
  useDistribution,
  useDeviceClassMix,
  useCapabilities,
  useDailyChanges,
  useDailyChurn,
} from '@/api/dashboard'
import Card from '@/design-system/Card.vue'
import SegmentedControl from '@/design-system/SegmentedControl.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import CompositionBar from '@/design-system/CompositionBar.vue'
import CapabilityBars from '@/design-system/CapabilityBars.vue'
import ChangeTimeSeries from '@/design-system/ChangeTimeSeries.vue'
import ChurnTimeSeries from '@/design-system/ChurnTimeSeries.vue'
import VendorMovementCard from './VendorMovementCard.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import DataFreshnessCard from './DataFreshnessCard.vue'
import { useFreshness } from '@/features/imports/useImportQueries'
import { flaggedDays, useFeedQuality } from '@/api/quality'
import { useTacVersions } from '@/features/imports/useTacVersions'
import { formatFull, formatPercent } from '@/lib/format'
import { useFilterState, FILTER_LABELS } from './useFilterState'

const { filters, activeFilters, setFilter, toggleUnknownDevice, clearAll, countBy, setCountBy } =
  useFilterState()

/**
 * The three measures, each with the caveat it carries.
 *
 * Subscribers is the one that needs explaining: the values overlap, because a person who
 * owns a Samsung and an Apple handset is counted under both. The percentages therefore
 * sum past 100%, which is correct but surprising if left unannounced.
 */
const COUNT_BY_OPTIONS = [
  { value: 'bindings' as const, label: 'Bindings' },
  { value: 'subscribers' as const, label: 'Subscribers' },
  { value: 'handsets' as const, label: 'Handsets' },
]

const countByLabel = computed(() => countBy.value)

const kpi = useKpiSummary(filters)
const vendors = useTopDimension('vendorCanonical', filters, 10, countBy)
const deviceTypes = useDistribution('deviceType', filters, countBy)
// Ten rows, as Device types beside it shows: with eight, the pair could not line up and the OS card
// carried a 64px blank band under its table at xl (measured 2026-10-07).
const operatingSystems = useTopDimension('operatingSystem', filters, 10, countBy)
const classMix = useDeviceClassMix(countBy)
const capabilities = useCapabilities(countBy)
const dailyChanges = useDailyChanges()
const dailyChurn = useDailyChurn()

/**
 * Days the source never delivered, from the platform's own calendar of expected days.
 *
 * This was a hard-coded list of seven dates. It happened to be right, which is exactly the
 * problem: it would have stayed right-looking after the eighth day went missing. The calendar
 * lives in imports.expected_business_date and the gap is computed against what actually
 * imported, so the chart's footnote cannot drift away from the chart.
 */
const { can } = useAuth()
const canSeeImports = computed(() => can(Permission.ImportView))
const freshness = useFreshness({ enabled: canSeeImports })

/*
  Days whose file looked unlike the ordinary days - shifted IMEIs, SIMs with several numbers -
  shaded on both daily charts. Without import.view there is nothing to shade, and the charts are
  drawn as before.
*/
const feedQuality = useFeedQuality({ enabled: canSeeImports })
const flagged = computed(() => flaggedDays(feedQuality.data.value))

const missingDays = computed(
  () => freshness.data.value?.find((f) => f.sourceCode === 'SQM')?.missingBusinessDates ?? [],
)

const coverage = computed(() => {
  const rows = dailyChanges.data.value
  if (!rows?.length) return null
  return { from: rows[0]!.date, to: rows[rows.length - 1]!.date, days: rows.length }
})

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
const models = useTopDimension('marketingName', modelFilters, 10, countBy)

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

/**
 * Two cards read marts that carry no dimension breakdown, so they cannot be filtered at all.
 *
 * `agg_device_class_daily` and `agg_capability_daily` are rolled up to (delivery, measure, class)
 * — there is no vendor or OS in them to filter on, and answering from the raw table would cost
 * seconds per card. That is a defensible design; showing network-wide figures under a heading
 * the page has just labelled "Filtered by Vendor: Samsung" is not. So they say so.
 */
const NETWORK_WIDE_NOTE = 'Network-wide — the filters above do not apply to this card.'

const classMixSubtitle = computed(() =>
  `Counted by ${countBy.value}. IoT/M2M is a real segment here, not tail noise.`
  + (isFiltered.value ? ` ${NETWORK_WIDE_NOTE}` : ''),
)

const capabilitySubtitle = computed(() =>
  `Counted by ${countBy.value}, from the GSMA band list and eUICC records.`
  + (isFiltered.value ? ` ${NETWORK_WIDE_NOTE}` : ''),
)

/**
 * The size of the GSMA database, read from the active version rather than written down.
 *
 * It was the literal 270,166 until that stopped being true: activating the September 16 export
 * took it to 270,885 and the sentence kept asserting the old figure. A number in prose is a
 * number nobody updates.
 */
const tacVersions = useTacVersions({ enabled: canSeeImports })

const activeTacRows = computed(
  () => tacVersions.data.value?.find((v) => v.status === 'Active')?.rowCount ?? null,
)
</script>

<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Device population</h1>
        <p class="mt-0.5 text-xs text-[var(--c-text-muted)]">
          Active device–SIM bindings across the network, enriched with GSMA device data.
        </p>
        <p class="mt-1 text-2xs text-[var(--c-text-muted)]">
          A <strong>binding</strong> is one number + SIM + handset combination
          (<code class="font-mono">MSISDN</code> +
          <code class="font-mono">IMSI</code> +
          <code class="font-mono">IMEI</code>). One subscriber can hold several.
        </p>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <!-- A segmented control rather than a dropdown: three options users switch between
             constantly, and showing all three keeps the distinction present rather than hidden
             behind a click. -->
        <SegmentedControl
          :model-value="countBy"
          :options="COUNT_BY_OPTIONS"
          label="Count breakdowns by"
          @update:model-value="setCountBy"
        />

        <!-- py-1: the 26px of the segmented control beside it, so the row's controls line up. -->
        <button
          type="button"
          class="rounded-[var(--radius-md)] border px-2.5 py-1 text-xs font-medium hover:bg-[var(--c-surface-hover)]"
          :class="filters.includeUnknownDevice ? '' : 'bg-[var(--c-surface-sunken)]'"
          @click="toggleUnknownDevice"
        >
          {{ filters.includeUnknownDevice ? 'Hide unknown devices' : 'Show unknown devices' }}
        </button>
      </div>
    </header>

    <!--
      Data freshness, directly under the title.

      Every figure on this page is only as trustworthy as the date this card reports. A vendor
      share means one thing if it describes yesterday and something else entirely if the last
      successful import was six weeks ago, and nothing else on the screen can tell the reader
      which - so it goes above the numbers rather than in a footer under them.
    -->
    <DataFreshnessCard v-if="canSeeImports" />

    <!-- Active filters. Shown as removable chips so the current view is always legible. -->
    <div v-if="isFiltered" class="flex flex-wrap items-center gap-2">
      <span class="text-xs text-[var(--c-text-muted)]">Filtered by</span>
      <button
        v-for="f in activeFilters"
        :key="f.key"
        type="button"
        class="group inline-flex items-center gap-1.5 rounded-full bg-[var(--c-accent-subtle)] py-1 pr-2 pl-2.5 text-xs font-medium"
        :title="`Remove ${FILTER_LABELS[f.key]} filter`"
        @click="setFilter(f.key, undefined)"
      >
        <span class="text-[var(--c-text-secondary)]">{{ FILTER_LABELS[f.key] }}:</span>
        <span>{{ f.value }}</span>
        <span aria-hidden="true" class="text-[var(--c-text-muted)] group-hover:text-[var(--c-text)]">×</span>
      </button>
      <button
        type="button"
        class="rounded-[var(--radius-sm)] px-2 py-1 text-xs font-medium text-[var(--c-text-secondary)] underline-offset-2 hover:underline"
        @click="clearAll"
      >
        Clear all
      </button>
      <span class="text-2xs text-[var(--c-text-muted)]">
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
        <!--
          Same accumulation as Handsets, smaller but not small: a number stays counted until the
          source removes it. Measured by how recently the feed last said anything about each
          number - 27.7% have NEVER appeared in a daily file, and only 36.1% were confirmed in the
          last 30 days. "still counted" is the honest short form.
        -->
        <KpiCard
          label="Subscribers"
          :value="kpi.data.value?.distinctSubscribers"
          identifier="MSISDN"
          qualifier="approx. · incl. dormant"
          definition="Distinct phone numbers with at least one active binding - not a count of numbers in use today. A binding stays active until the source removes it, so a number that has gone quiet is still counted. Measured by when the feed last mentioned each number: 27.7% have never appeared in a daily file at all, coming from the initial dump; 36.1% were confirmed in the last 30 days, 22.3% within 90, and 13.9% not for over 90 days. Estimated with HyperLogLog (~0.5% error); exact counts are available via export."
        />
        <KpiCard
          label="SIM cards"
          :value="kpi.data.value?.distinctSims"
          identifier="IMSI"
          qualifier="approx. · incl. dormant"
          definition="Distinct SIM identities with an active binding - not a count of SIMs in use today, for the same reason as Subscribers: 28.2% have never appeared in a daily file, 35.8% were confirmed in the last 30 days. Slightly higher than the number of subscribers because a number swapped to a new SIM has more than one IMSI - measured at 1.0017 SIMs per number, with 97.8% of numbers holding exactly one."
        />
        <!--
          The qualifier says "includes replaced" because this figure is not what its label
          suggests, and the difference is large enough to change a conclusion.

          It counts IMEIs holding an ACTIVE binding, and a binding stays active until the source
          removes it - so a phone somebody replaced is still in here. Measured: 1.079 handsets per
          SIM across the whole population, against 0.996 for bindings the feed confirmed within
          the last 30 days, where the ratio is the 1:1 it should be. The gap is accumulated
          history, not devices.

          Stated on the face of the card rather than only in the tooltip: a reader who never
          hovers would otherwise read it as "phones on the network today", which is the exact
          misreading that prompted this.
        -->
        <KpiCard
          label="Handsets"
          :value="kpi.data.value?.distinctDevices"
          identifier="IMEI"
          qualifier="approx. · includes replaced"
          definition="Distinct 14-digit IMEIs holding an active binding - NOT a count of handsets in use today. A binding stays active until the source removes it, so a phone that has been replaced is still counted. Measured: 1.079 handsets per SIM overall, against 0.996 for bindings confirmed in the last 30 days, where the ratio is 1:1 as expected; 45% of active bindings have never appeared in a daily file at all, coming from the initial dump, which covers a 30-day window rather than an instant. A dual-SIM phone does not lower this: the GSMA record gives 77% of bindings two IMEIs, one per radio. Excludes the 000000 sentinel, which is one literal value and not a handset."
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

    <!-- Daily change over time -->
    <Card
      title="Daily change"
      :subtitle="coverage
        ? `${coverage.days} days, ${coverage.from} to ${coverage.to}. Bars are the day's flow; the line is the running total.`
        : 'Daily flow and running total.'"
    >
      <AsyncBoundary
        :is-loading="dailyChanges.isPending.value"
        :is-error="dailyChanges.isError.value"
        :error="dailyChanges.error.value"
        :is-empty="dailyChanges.data.value?.length === 0"
        empty-message="No daily files loaded yet."
        min-height="20rem"
        @retry="dailyChanges.refetch()"
      >
        <ChangeTimeSeries :data="dailyChanges.data.value ?? []" :flagged="flagged" />
      </AsyncBoundary>

      <template #footer>
        <p class="text-2xs text-[var(--c-text-muted)]">
          <template v-if="missingDays.length">
            <strong>{{ missingDays.length }} days are missing</strong> from the source and appear as
            gaps rather than interpolated points: {{ [...missingDays].sort().join(', ') }}.
          </template>
          <template v-else>Every expected day in this range was delivered and imported.</template>
        </p>
        <p v-if="flagged.size" class="mt-1.5 text-2xs text-[var(--c-text-muted)]">
          <strong>{{ flagged.size }} shaded days</strong> are ones whose file looked unlike the ordinary
          days - IMEIs shifted by a digit, or SIMs carrying several numbers. Their figures may be
          distorted; hover a day for what was found.
        </p>
      </template>
    </Card>

    <!-- Churn -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card
        title="SIM and handset changes"
        subtitle="Subscribers who moved to a different SIM or a different device, per day."
      >
        <AsyncBoundary
          :is-loading="dailyChurn.isPending.value"
          :is-error="dailyChurn.isError.value"
          :error="dailyChurn.error.value"
          :is-empty="dailyChurn.data.value?.length === 0"
          empty-message="No daily files loaded yet."
          min-height="18rem"
          @retry="dailyChurn.refetch()"
        >
          <ChurnTimeSeries :data="dailyChurn.data.value ?? []" :flagged="flagged" />
        </AsyncBoundary>

        <template #footer>
          <p class="text-2xs text-[var(--c-text-muted)]">
            Same-day definition: the number has a remove carrying one SIM (or handset) and an add
            carrying another on the same date. A change spanning midnight is not counted, so these
            are a floor, not a total.
          </p>
          <p v-if="flagged.size" class="mt-1.5 text-2xs text-[var(--c-text-muted)]">
            Shaded days: the feed itself looked wrong. From 2026-07-27 SIMs began carrying several
            numbers a day, and from 2026-09-15 IMEIs arrived shifted by a digit; both show here as SIM
            and handset changes that did not happen.
          </p>
        </template>
      </Card>

      <VendorMovementCard />
    </div>

    <!-- Composition -->
    <Card
      title="Device class mix"
      :subtitle="classMixSubtitle"
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
      title="Network &amp; SIM capability"
      :subtitle="capabilitySubtitle"
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
        <p class="text-2xs text-[var(--c-text-muted)]">
          <strong>VoLTE is not shown</strong> because the GSMA dataset does not contain it: the band
          list mentions VoLTE in 2 records<template v-if="activeTacRows"> of
          {{ formatFull(activeTacRows) }}</template>, and the IMS fields describe emergency calling
          rather than VoLTE. A proxy would look like an answer without being one.
        </p>
      </template>
    </Card>

    <!-- Vendors and models -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card title="Top vendors" :subtitle="`Counted by ${countByLabel}. Click a bar to filter.`">
        <AsyncBoundary
          :is-loading="vendors.isPending.value"
          :is-error="vendors.isError.value"
          :error="vendors.error.value"
          :is-empty="vendors.data.value?.length === 0"
          min-height="18rem"
          @retry="vendors.refetch()"
        >
          <BarChart
            :data="vendors.data.value ?? []"
            :unit="countByLabel"
            @select="(v) => drillInto('vendor', v)"
          />
        </AsyncBoundary>

        <template #footer>
          <p v-if="countBy === 'bindings'" class="text-2xs text-[var(--c-text-muted)]">
            <strong>Bindings</strong> are number + SIM + handset combinations. A dual-SIM phone
            serving two numbers counts twice, so this runs higher than the handset count for the
            same vendor. Switch to Handsets to compare.
          </p>
          <p
            v-else-if="countBy === 'subscribers'"
            class="text-2xs text-[var(--c-text-muted)]"
          >
            <strong>Subscribers</strong> are distinct phone numbers, and these values
            <strong>overlap</strong>: someone owning a Samsung and an Apple handset is counted under
            both. The percentages therefore sum to more than 100%.
          </p>
          <p v-else class="text-2xs text-[var(--c-text-muted)]">
            <strong>Handsets</strong> are distinct IMEIs. These do not overlap &mdash; a handset
            belongs to exactly one vendor &mdash; so the values add up. Excludes the
            <code class="font-mono">000000</code> population, which has no handset to
            count.
          </p>
        </template>
      </Card>

      <Card title="Top models" :subtitle="`Counted by ${countByLabel}. Excludes unknown devices.`">
        <AsyncBoundary
          :is-loading="models.isPending.value"
          :is-error="models.isError.value"
          :error="models.error.value"
          :is-empty="models.data.value?.length === 0"
          min-height="18rem"
          @retry="models.refetch()"
        >
          <BarChart :data="models.data.value ?? []" :unit="countByLabel" />
        </AsyncBoundary>
      </Card>
    </div>

    <!-- Types and OS -->
    <div class="grid gap-5 xl:grid-cols-2">
      <Card title="Device types" :subtitle="`Counted by ${countByLabel}. Click to filter.`" flush>
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
            :unit="countByLabel"
            @select="(v) => drillInto('deviceType', v)"
          />
        </AsyncBoundary>
      </Card>

      <Card title="Operating systems" :subtitle="`Counted by ${countByLabel}. Normalised for case and whitespace.`" flush>
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
            :unit="countByLabel"
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
              <dt class="text-xs text-[var(--c-text-muted)]">Enriched from GSMA</dt>
              <dd class="mt-1 text-lg font-semibold tabular text-[var(--c-success-text)]">
                {{ formatFull(kpi.data.value?.tacMatchedBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
                {{ formatPercent(kpi.data.value?.tacCoveragePercent ?? 0, 2) }} of active bindings.
              </dd>
            </div>

            <div>
              <dt class="text-xs text-[var(--c-text-muted)]">
                Unknown device (<code class="font-mono">000000</code>)
              </dt>
              <dd class="mt-1 text-lg font-semibold tabular text-[var(--c-warning-text)]">
                {{ formatFull(kpi.data.value?.unknownDeviceBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
                A sentinel the source uses for "device not known". Expected, not a defect.
              </dd>
            </div>

            <div>
              <dt class="text-xs text-[var(--c-text-muted)]">Malformed IMEI</dt>
              <dd class="mt-1 text-lg font-semibold tabular text-[var(--c-danger-text)]">
                {{ formatFull(kpi.data.value?.malformedImeiBindings ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
                Numeric but not 14 digits. <strong>The only genuine defect here</strong> — worth raising
                with the source.
              </dd>
            </div>

            <div>
              <dt class="text-xs text-[var(--c-text-muted)]">Unregistered TAC</dt>
              <dd class="mt-1 text-lg font-semibold tabular">
                {{ formatFull(unregisteredTac ?? 0) }}
              </dd>
              <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
                Well-formed IMEI whose TAC is absent from the GSMA database.
              </dd>
            </div>
          </dl>

          <p class="border-t pt-3 text-xs text-[var(--c-text-muted)]">
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
