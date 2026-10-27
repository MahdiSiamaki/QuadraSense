<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useEsim, useIot, usePlanningStatus, useSunset, type PlanningClass, type PlanningFilter, type PlanningModelTrendPoint, type PlanningTrendPoint } from '@/api/planning'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Card from '@/design-system/Card.vue'
import SegmentedControl, { tabId } from '@/design-system/SegmentedControl.vue'
import TextField from '@/design-system/TextField.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import { formatDate, formatDateTime, formatFull, formatPercent, formatSequence } from '@/lib/format'
import CampaignRequestDialog from './CampaignRequestDialog.vue'
import PlanningBreakdown from './PlanningBreakdown.vue'
import PlanningNumbers from './PlanningNumbers.vue'
import PlanningTrendChart from './PlanningTrendChart.vue'

/**
 * Network and product planning: what the devices on the network can do, counted by the numbers that
 * hold them (docs/architecture/19-network-planning.md).
 *
 * The page decides nothing. Every class comes from the server, from the planning run the import
 * worker built for a delivery; the page shows it, narrows it by vendor, model or device type, and
 * says plainly what the data cannot answer. The tab and the filters are in the address; numbers never
 * are.
 */
const { can } = useAuth()
const route = useRoute()
const router = useRouter()

type Tab = 'sunset' | 'esim' | 'iot'
const TABS: { value: Tab; label: string }[] = [
  { value: 'sunset', label: '2G/3G sunset' },
  { value: 'esim', label: 'eSIM' },
  { value: 'iot', label: 'IoT fleet' },
]

const tab = computed<Tab>(() => (TABS.some((t) => t.value === route.query['tab']) ? (route.query['tab'] as Tab) : 'sunset'))
const filter = computed<PlanningFilter>(() => ({
  vendor: (route.query['vendor'] as string) || undefined,
  model: (route.query['model'] as string) || undefined,
  deviceType: (route.query['deviceType'] as string) || undefined,
}))

function setQuery(patch: Record<string, string | undefined>) {
  void router.replace({ query: { ...route.query, ...patch } })
}

// The filter is typed here and applied on purpose: a half-typed vendor is no filter.
const draft = ref({ vendor: '', model: '', deviceType: '' })
watch(filter, (f) => (draft.value = { vendor: f.vendor ?? '', model: f.model ?? '', deviceType: f.deviceType ?? '' }), { immediate: true })
function applyFilter() {
  setQuery({
    vendor: draft.value.vendor.trim() || undefined,
    model: draft.value.model.trim() || undefined,
    deviceType: draft.value.deviceType.trim() || undefined,
  })
}
function clearFilter() {
  setQuery({ vendor: undefined, model: undefined, deviceType: undefined })
}

const status = usePlanningStatus()
const data = computed(() => status.data.value ?? null)
const runs = computed(() => data.value?.runs ?? [])
const runId = ref<number | null>(null)
const run = computed(() => runs.value.find((r) => r.runId === runId.value) ?? runs.value[0] ?? null)
const ready = computed(() => !!run.value)

const sunset = useSunset(() => run.value?.runId ?? null, filter, () => ready.value && tab.value === 'sunset')
const esim = useEsim(() => run.value?.runId ?? null, filter, () => ready.value && tab.value === 'esim')
const iot = useIot(() => run.value?.runId ?? null, filter, () => ready.value && tab.value === 'iot')

/** The class the breakdown and the list below the cards show. */
const chosen = ref<Record<'sunset' | 'esim', string>>({ sunset: 'exposed', esim: 'capable' })
const reason = ref<string | null>(null)

const SUNSET_COLORS: Record<string, string> = {
  'ready-lte': '--viz-3',
  'ready-lpwan': '--viz-2',
  exposed: '--viz-6',
  'not-assessable': '--viz-null',
}
const ESIM_COLORS: Record<string, string> = { capable: '--viz-1', 'not-capable': '--viz-5', 'not-stated': '--viz-null' }

function series(classes: PlanningClass[] | undefined, colors: Record<string, string>) {
  return (classes ?? []).map((c) => ({ key: c.class, label: c.label, color: colors[c.class] ?? '--viz-null' }))
}

function trendPoints(points: PlanningTrendPoint[] | undefined) {
  const byRun = new Map<string, { label: string; values: Record<string, number> }>()
  for (const p of points ?? []) {
    const label = formatSequence(p.asOfSequence, p.asOfDate)
    const entry = byRun.get(label) ?? { label, values: {} }
    entry.values[p.class] = (entry.values[p.class] ?? 0) + p.numbers
    byRun.set(label, entry)
  }
  return [...byRun.values()]
}

function modelPoints(points: PlanningModelTrendPoint[] | undefined) {
  const bySeq = new Map<number, { label: string; values: Record<string, number> }>()
  for (const p of points ?? []) {
    const entry = bySeq.get(p.sequence) ?? { label: formatSequence(p.sequence, p.date), values: {} }
    entry.values[p.class] = (entry.values[p.class] ?? 0) + p.bindings
    bySeq.set(p.sequence, entry)
  }
  return [...bySeq.entries()].sort((a, b) => a[0] - b[0]).map(([, v]) => v)
}

const canNumbers = computed(() => can(Permission.LookupSubscriber))
const canCampaign = computed(() => can(Permission.CampaignExport) && can(Permission.LookupSubscriber))
const requesting = ref<'sunset' | 'esim' | null>(null)

const requestLabel = computed(() => {
  if (requesting.value === 'esim') return esim.data.value?.classes.find((c) => c.class === chosen.value.esim)?.label ?? ''
  const label = sunset.data.value?.classes.find((c) => c.class === chosen.value.sunset)?.label ?? ''
  const why = sunset.data.value?.notAssessable.find((r) => r.class === reason.value)?.label
  return why ? `${label}: ${why}` : label
})
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">Network planning</h1>
        <p class="mt-0.5 max-w-3xl text-sm text-[var(--c-text-secondary)]">
          What the handsets on the network can use, counted by the numbers that hold them: who a 2G/3G switch-off
          strands, who has an eSIM-capable handset, and how the IoT fleet stands. From the GSMA catalogue's own
          statements - what a record does not state is counted as unknown, never as “no”.
        </p>
      </div>
      <div v-if="data?.catalogue" class="flex flex-wrap gap-2 text-xs text-[var(--c-text-secondary)]">
        <span
          class="tabular inline-flex items-center rounded-full border px-2.5 py-1"
          :title="`${formatFull(data.catalogue.unparsedModels)} models carry a band entry no rule recognises; they are kept as written, not guessed.`"
        >
          GSMA version {{ data.catalogue.tacVersionId }} · {{ formatFull(data.catalogue.models) }} models
        </span>
      </div>
    </header>

    <AsyncBoundary
      :is-loading="status.isPending.value"
      :is-error="status.isError.value"
      :error="status.error.value"
      min-height="12rem"
      @retry="status.refetch()"
    >
      <template v-if="data">
        <p
          v-if="!data.deployed || !run"
          class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-sm text-[var(--c-text-secondary)]"
          role="status"
        >
          <template v-if="!data.deployed">The planning tables are not installed yet (analytics migrations 029 and 030).</template>
          <template v-else-if="data.running">
            The first planning run is being built for delivery
            {{ formatSequence(data.running.asOfSequence, data.running.asOfDate) }}:
            {{ data.running.chunksDone }} of {{ data.running.chunksTotal }} parts written. The import worker builds it while idle.
          </template>
          <template v-else>No planning run is published yet. The import worker builds one while it is idle.</template>
        </p>

        <template v-if="run">
          <p
            v-if="data.stale"
            class="rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] px-4 py-3 text-sm text-[var(--c-text)]"
            role="status"
          >
            {{ data.stale }}
          </p>

          <div class="flex flex-wrap items-end gap-3">
            <label class="flex flex-col gap-1 text-xs text-[var(--c-text-secondary)]">
              Delivery
              <select
                class="h-9 rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 text-sm text-[var(--c-text)]"
                :value="run.runId"
                @change="runId = Number(($event.target as HTMLSelectElement).value)"
              >
                <option v-for="r in runs" :key="r.runId" :value="r.runId">
                  {{ formatSequence(r.asOfSequence, r.asOfDate) }}{{ r.hasNumbers ? '' : ' (figures only)' }}
                </option>
              </select>
            </label>
            <TextField v-model="draft.vendor" label="Vendor" placeholder="Any" class="w-40" />
            <TextField v-model="draft.model" label="Model" placeholder="Any" class="w-48" />
            <TextField v-model="draft.deviceType" label="Device type" placeholder="Any" class="w-40" />
            <Button size="sm" @click="applyFilter">Apply</Button>
            <Button v-if="filter.vendor || filter.model || filter.deviceType" size="sm" variant="ghost" @click="clearFilter">Clear</Button>
          </div>

          <SegmentedControl
            :model-value="tab"
            class="w-fit"
            tabs
            panel-id="planning-panel"
            :options="TABS"
            label="Planning view"
            @update:model-value="(next) => setQuery({ tab: next })"
          />

          <section id="planning-panel" role="tabpanel" :aria-labelledby="tabId('planning-panel', TABS.findIndex((t) => t.value === tab))" class="flex flex-col gap-4">
            <!-- ====================================================== N1 -->
            <template v-if="tab === 'sunset'">
              <p v-if="sunset.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
              <template v-else-if="sunset.data.value">
                <p v-if="sunset.data.value.filtered" class="text-xs text-[var(--c-text-secondary)]">
                  Narrowed to numbers holding such a handset: their class is still decided by all their handsets, and the figures are
                  estimated.
                </p>
                <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                  <button
                    v-for="c in sunset.data.value.classes"
                    :key="c.class"
                    type="button"
                    class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4 text-left transition-colors hover:border-[var(--c-border-strong)]"
                    :class="chosen.sunset === c.class ? 'border-[var(--c-accent)] ring-1 ring-[var(--c-accent)]' : ''"
                    :aria-pressed="chosen.sunset === c.class"
                    @click="chosen.sunset = c.class; reason = null"
                  >
                    <p class="flex items-center gap-2 text-xs text-[var(--c-text-secondary)]">
                      <span class="size-2 rounded-full" :style="{ background: `var(${SUNSET_COLORS[c.class]})` }" aria-hidden="true" />
                      {{ c.label }}
                    </p>
                    <p class="tabular mt-1 text-2xl font-semibold">{{ formatFull(c.numbers) }}</p>
                    <p class="tabular text-2xs text-[var(--c-text-muted)]">
                      {{ formatPercent(c.sharePercent) }} of numbers · {{ formatFull(c.bindings) }} bindings · ~{{ formatFull(c.handsets) }} handsets
                    </p>
                  </button>
                </div>

                <div class="grid gap-3 lg:grid-cols-2">
                  <Card title="Not assessable, by reason" subtitle="The first reason that applies to each number. None of these is counted as exposed or ready.">
                    <table class="w-full text-xs">
                      <tbody>
                        <tr v-for="r in sunset.data.value.notAssessable" :key="r.class" class="border-t first:border-t-0">
                          <td class="py-1.5 pr-2">
                            <button
                              type="button"
                              class="text-left hover:underline"
                              :class="reason === r.class ? 'font-medium text-[var(--c-accent)]' : ''"
                              @click="chosen.sunset = 'not-assessable'; reason = r.class"
                            >
                              {{ r.label }}
                            </button>
                          </td>
                          <td class="tabular py-1.5 text-right">{{ formatFull(r.numbers) }}</td>
                          <td class="tabular py-1.5 pl-2 text-right text-[var(--c-text-muted)]">{{ formatPercent(r.sharePercent) }}</td>
                        </tr>
                      </tbody>
                    </table>
                  </Card>
                  <Card title="Inside the exposed class">
                    <dl class="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1.5 text-xs">
                      <dt class="text-[var(--c-text-secondary)]">GSM (2G) only - stranded by a 2G switch-off too</dt>
                      <dd class="tabular text-right">{{ formatFull(sunset.data.value.exposedGsmOnly) }}</dd>
                      <dt class="text-[var(--c-text-secondary)]">Holding an IoT / M2M device</dt>
                      <dd class="tabular text-right">{{ formatFull(sunset.data.value.exposedIot) }}</dd>
                      <dt class="text-[var(--c-text-secondary)]" title="Same TAC and a serial one apart, or a TAC 100 apart and the same serial, on the same number">
                        Handsets assessed through their dual-SIM partner
                      </dt>
                      <dd class="tabular text-right">{{ formatFull(sunset.data.value.paired) }}</dd>
                    </dl>
                    <div v-for="n in sunset.data.value.noData" :key="n.topic" class="mt-3 border-t pt-3 text-xs">
                      <p class="font-medium">{{ n.topic }}: no data</p>
                      <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
                    </div>
                  </Card>
                </div>

                <div class="grid gap-3 lg:grid-cols-2">
                  <Card title="Numbers by class, per planning run" subtitle="One point per delivery a run was built for; they begin with the first run.">
                    <PlanningTrendChart
                      v-if="!sunset.data.value.filtered && sunset.data.value.trend.length"
                      :points="trendPoints(sunset.data.value.trend)"
                      :series="series(sunset.data.value.classes, SUNSET_COLORS)"
                      unit="numbers"
                    />
                    <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">
                      {{ sunset.data.value.filtered ? 'The trend is unfiltered only: older runs keep their totals, not their models.' : 'No run yet.' }}
                    </p>
                  </Card>
                  <Card
                    title="Bindings on each model class, per delivery"
                    subtitle="Per model, from the dashboard's own mart, under today's catalogue - bindings, not numbers."
                  >
                    <PlanningTrendChart
                      :points="modelPoints(sunset.data.value.modelTrend)"
                      :series="series(sunset.data.value.classes, SUNSET_COLORS)"
                      unit="bindings"
                    />
                  </Card>
                </div>

                <Card :title="`Who holds what: ${sunset.data.value.classes.find((c) => c.class === chosen.sunset)?.label}`">
                  <PlanningBreakdown
                    view="sunset"
                    :planning-class="chosen.sunset"
                    :run="run.runId"
                    :filter="filter"
                    :can-export="can(Permission.DataExport)"
                    :can-open-device="can(Permission.DeviceView)"
                  />
                </Card>

                <Card v-if="canNumbers && run.hasNumbers" title="The numbers">
                  <div v-if="canCampaign" class="mb-2 flex justify-end">
                    <Button size="sm" variant="secondary" @click="requesting = 'sunset'">Request a campaign list</Button>
                  </div>
                  <PlanningNumbers view="sunset" :planning-class="chosen.sunset" :reason="reason" :run="run.runId" :filter="filter" />
                </Card>
              </template>
              <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
            </template>

            <!-- ====================================================== N3 -->
            <template v-else-if="tab === 'esim'">
              <p v-if="esim.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
              <template v-else-if="esim.data.value">
                <div class="grid gap-3 sm:grid-cols-3">
                  <button
                    v-for="c in esim.data.value.classes"
                    :key="c.class"
                    type="button"
                    class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4 text-left transition-colors hover:border-[var(--c-border-strong)]"
                    :class="chosen.esim === c.class ? 'border-[var(--c-accent)] ring-1 ring-[var(--c-accent)]' : ''"
                    :aria-pressed="chosen.esim === c.class"
                    @click="chosen.esim = c.class"
                  >
                    <p class="flex items-center gap-2 text-xs text-[var(--c-text-secondary)]">
                      <span class="size-2 rounded-full" :style="{ background: `var(${ESIM_COLORS[c.class]})` }" aria-hidden="true" />
                      {{ c.label }}
                    </p>
                    <p class="tabular mt-1 text-2xl font-semibold">{{ formatFull(c.numbers) }}</p>
                    <p class="tabular text-2xs text-[var(--c-text-muted)]">
                      {{ formatPercent(c.sharePercent) }} of numbers · {{ formatFull(c.bindings) }} bindings · ~{{ formatFull(c.handsets) }} handsets
                    </p>
                  </button>
                </div>
                <p class="text-2xs text-[var(--c-text-muted)]">
                  “Not stated” is mostly older handsets: GSMA records allocated before 2019 carry “00” in the eUICC fields, which says
                  nothing - it is not read as “no eSIM”.
                </p>

                <div v-for="n in esim.data.value.noData" :key="n.topic" class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-xs">
                  <p class="font-medium">{{ n.topic }}: no data</p>
                  <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
                </div>

                <div class="grid gap-3 lg:grid-cols-2">
                  <Card title="Numbers by class, per planning run">
                    <PlanningTrendChart
                      v-if="!esim.data.value.filtered && esim.data.value.trend.length"
                      :points="trendPoints(esim.data.value.trend)"
                      :series="series(esim.data.value.classes, ESIM_COLORS)"
                      unit="numbers"
                    />
                    <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">
                      {{ esim.data.value.filtered ? 'The trend is unfiltered only.' : 'No run yet.' }}
                    </p>
                  </Card>
                  <Card title="Bindings on eSIM-capable models, per delivery" subtitle="Per model, under today's catalogue - bindings, not numbers.">
                    <PlanningTrendChart
                      :points="modelPoints(esim.data.value.modelTrend)"
                      :series="series(esim.data.value.classes, ESIM_COLORS)"
                      unit="bindings"
                    />
                  </Card>
                </div>

                <Card :title="`Who holds what: ${esim.data.value.classes.find((c) => c.class === chosen.esim)?.label}`">
                  <PlanningBreakdown
                    view="esim"
                    :planning-class="chosen.esim"
                    :run="run.runId"
                    :filter="filter"
                    :can-export="can(Permission.DataExport)"
                    :can-open-device="can(Permission.DeviceView)"
                  />
                </Card>

                <Card v-if="canNumbers && run.hasNumbers" title="The numbers">
                  <div v-if="canCampaign" class="mb-2 flex justify-end">
                    <Button size="sm" variant="secondary" @click="requesting = 'esim'">Request a campaign list</Button>
                  </div>
                  <PlanningNumbers view="esim" :planning-class="chosen.esim" :run="run.runId" :filter="filter" />
                </Card>
              </template>
              <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
            </template>

            <!-- ====================================================== N5 -->
            <template v-else>
              <p v-if="iot.isError.value" class="text-sm text-[var(--c-danger-text)]" role="alert">Not available.</p>
              <template v-else-if="iot.data.value">
                <div class="grid gap-3 sm:grid-cols-2">
                  <Card :title="iot.data.value.devices.label" subtitle="Modules, modems, dongles, routers, IoT devices and vehicle units, by the GSMA device type">
                    <p class="tabular text-2xl font-semibold">~{{ formatFull(iot.data.value.devices.handsets) }} <span class="text-sm font-normal text-[var(--c-text-secondary)]">devices</span></p>
                    <p class="tabular text-2xs text-[var(--c-text-muted)]">
                      ~{{ formatFull(iot.data.value.devices.numbers) }} numbers ({{ formatPercent(iot.data.value.devices.sharePercent) }} of numbers) ·
                      {{ formatFull(iot.data.value.devices.bindings) }} bindings
                    </p>
                  </Card>
                  <Card :title="iot.data.value.notClassified.label">
                    <p class="tabular text-2xl font-semibold">{{ formatFull(iot.data.value.notClassified.bindings) }} <span class="text-sm font-normal text-[var(--c-text-secondary)]">bindings</span></p>
                    <p class="text-2xs text-[var(--c-text-muted)]">Neither phone nor IoT can be said of these: they are left out of both, not guessed.</p>
                  </Card>
                </div>

                <div class="grid gap-3 lg:grid-cols-3">
                  <Card v-for="block in [
                    { title: 'By device type', rows: iot.data.value.byDeviceType, unit: 'Devices' },
                    { title: '2G and 2G/3G exposure', rows: iot.data.value.exposure, unit: 'Devices' },
                    { title: 'SIMs carried, all time', rows: iot.data.value.simRotation, unit: 'Devices' },
                  ]" :key="block.title" :title="block.title" flush>
                    <table class="w-full text-xs">
                      <tbody>
                        <tr v-for="r in block.rows" :key="r.key" class="border-t first:border-t-0">
                          <td class="px-4 py-1.5">{{ r.label }}</td>
                          <td class="tabular px-4 py-1.5 text-right">
                            {{ formatFull(block.title === 'SIMs carried, all time' ? r.numbers : r.handsets) }}
                          </td>
                        </tr>
                        <tr v-if="!block.rows.length"><td class="px-4 py-3 text-[var(--c-text-muted)]">Not available.</td></tr>
                      </tbody>
                    </table>
                  </Card>
                </div>
                <p class="text-2xs text-[var(--c-text-muted)]">
                  Devices are distinct IMEIs, estimated. SIMs carried counts every SIM a device's bindings have had since the initial
                  dump, from the binding history; shifted IMEIs are not counted as devices.
                </p>

                <div v-for="n in iot.data.value.noData" :key="n.topic" class="rounded-[var(--radius-md)] border bg-[var(--c-surface-sunken)] px-4 py-3 text-xs">
                  <p class="font-medium">{{ n.topic }}: no data</p>
                  <p class="mt-0.5 text-[var(--c-text-secondary)]">{{ n.reason }}</p>
                </div>

                <Card title="IoT / M2M devices by vendor, model and type" subtitle="Module makers - Quectel, SIMCom, Fibocom and others - are the vendor here">
                  <PlanningBreakdown
                    view="iot"
                    planning-class="iot"
                    :run="run.runId"
                    :filter="filter"
                    :can-export="can(Permission.DataExport)"
                    :can-open-device="can(Permission.DeviceView)"
                  />
                </Card>
              </template>
              <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
            </template>
          </section>

          <CampaignRequestDialog
            v-if="canCampaign"
            :open="requesting !== null"
            :view="requesting ?? 'sunset'"
            :planning-class="requesting === 'esim' ? chosen.esim : chosen.sunset"
            :class-label="requestLabel"
            :reason="requesting === 'sunset' && chosen.sunset === 'not-assessable' ? reason : null"
            :run="run"
            :filter="filter"
            @close="requesting = null"
          />

          <details class="rounded-[var(--radius-md)] border px-4 py-3 text-xs text-[var(--c-text-secondary)]">
            <summary class="cursor-pointer font-medium text-[var(--c-text)]">How these are counted</summary>
            <ul class="mt-2 list-disc space-y-1 pl-5">
              <li>A <strong>number</strong> is an MSISDN; a <strong>binding</strong> is one number, one SIM and one handset together; a
                <strong>handset</strong> is an IMEI - a dual-SIM phone has two.</li>
              <li><strong>Active</strong> means the operator's last event for the binding, up to this delivery, was an add.</li>
              <li>Every share is of the numbers with an active binding at this delivery: {{ formatFull(sunset.data.value?.totalNumbers ?? esim.data.value?.totalNumbers ?? 0) }}.</li>
              <li>A number's class is decided by all its active handsets at once. One handset with LTE or 5G makes it ready.</li>
              <li>Handsets are distinct IMEIs counted with HyperLogLog (~0.5% error), so they are marked as estimated.</li>
              <li>Published {{ formatDateTime(run.publishedAt) }}, for delivery {{ formatSequence(run.asOfSequence, run.asOfDate) }}<template v-if="run.asOfDate">
                ({{ formatDate(run.asOfDate) }})</template>.</li>
            </ul>
          </details>
        </template>
      </template>
    </AsyncBoundary>
  </div>
</template>
