<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError, saveFile } from '@/api/client'
import {
  CHECK_LABELS,
  RULE_TITLES,
  useRiskExport,
  useRiskList,
  type RiskColumn,
  type RiskRow,
  type RiskRule,
  type RiskView,
} from '@/api/risk'
import Button from '@/design-system/Button.vue'
import Pagination from '@/design-system/Pagination.vue'
import { control, miniLabel, segment } from '@/features/explorer/ui'
import { formatDate, formatFull } from '@/lib/format'
import RiskLevelBadge from './RiskLevelBadge.vue'
import RiskDistributionChart from './RiskDistributionChart.vue'
import RiskDailyChart from './RiskDailyChart.vue'
import DimensionTable from '@/design-system/DimensionTable.vue'
import { useRiskDaily, useRiskDeviceTypes, useRiskDistribution } from '@/api/risk'

/**
 * One family's lists: pick a rule, see who is over its threshold, open one.
 *
 * The threshold box starts at the configured value and may be changed for this view only - the
 * server audits the number and never saves it. Below the storage floor it refuses, and says so.
 * The data-quality view lists what crossed only through feed defects, in neutral colours and under
 * a caption that says it is not behaviour.
 */
const props = defineProps<{ rules: RiskRule[]; canExport: boolean; initial?: string | null; deviceTypes: string[] }>()
const emit = defineEmits<{ drill: [identifier: string] }>()

const selected = ref(props.rules.find((r) => r.rule === props.initial)?.rule ?? props.rules[0]?.rule ?? '')
const rule = computed(() => props.rules.find((r) => r.rule === selected.value) ?? props.rules[0])
const view = ref<RiskView>('risk')
const threshold = ref<number | null>(null)
const tacsThreshold = ref<number | null>(null)
const page = ref(1)
const pageSize = ref(50)
/** One GSMA device type, or '' for all. Not offered for numbers, which have no handset. */
const deviceType = ref('')
const hasDeviceType = computed(() => rule.value !== undefined && rule.value.list !== 'Numbers')

const list = useRiskList()

// The analyses beside the list: where the threshold sits, and what the list is made of.
const ruleName = computed(() => rule.value?.rule ?? null)
const distribution = useRiskDistribution(ruleName)
const typeBreakdown = useRiskDeviceTypes(computed(() => (hasDeviceType.value ? ruleName.value : null)))
const daily = useRiskDaily(computed(() => rule.value?.list === 'Numbers'))
const showAnalysis = ref(true)

const deviceRows = computed(() => {
  const rows = typeBreakdown.data.value?.rows ?? []
  const total = rows.reduce((sum, r) => sum + r.entities, 0) || 1
  return rows.map((r) => ({ key: r.deviceType, count: r.entities, percent: (100 * r.entities) / total }))
})

function filterTo(type: string) {
  deviceType.value = deviceType.value === type ? '' : type
  apply()
}
const exporter = useRiskExport()
const result = computed(() => list.data.value ?? null)

function reset() {
  threshold.value = rule.value?.threshold ?? null
  tacsThreshold.value = rule.value?.tacsThreshold ?? null
}

function load() {
  if (!rule.value) return
  list.mutate({
    rule: rule.value.rule,
    view: view.value,
    threshold: threshold.value !== rule.value.threshold ? threshold.value : null,
    tacsThreshold: tacsThreshold.value !== rule.value.tacsThreshold ? tacsThreshold.value : null,
    page: page.value,
    pageSize: pageSize.value,
    deviceTypes: hasDeviceType.value && deviceType.value ? [deviceType.value] : [],
  })
}

watch(
  () => rule.value?.rule,
  () => {
    if (!rule.value?.hasDataQualityView) view.value = 'risk'
    reset()
    page.value = 1
    load()
  },
  { immediate: true },
)

function setView(v: RiskView) {
  view.value = v
  page.value = 1
  load()
}

function apply() {
  page.value = 1
  load()
}

function goTo(p: number) {
  page.value = p
  load()
}

function changeSize(size: number) {
  pageSize.value = size
  page.value = 1
  load()
}

async function exportCsv() {
  if (!rule.value) return
  const file = await exporter.mutateAsync({
    rule: rule.value.rule,
    view: view.value,
    threshold: threshold.value !== rule.value.threshold ? threshold.value : null,
    tacsThreshold: tacsThreshold.value !== rule.value.tacsThreshold ? tacsThreshold.value : null,
    deviceTypes: hasDeviceType.value && deviceType.value ? [deviceType.value] : [],
  })
  saveFile(file, `risk-${rule.value.rule}-${view.value}.csv`)
}

const error = computed(() => {
  const e = list.error.value
  if (!e) return null
  return e instanceof ApiError ? (e.problem?.detail ?? e.problem?.title ?? e.message) : 'The list could not be loaded.'
})

const belowFloor = computed(() => rule.value !== undefined && threshold.value !== null && threshold.value + 1 < rule.value.floor)

function cell(row: RiskRow, column: RiskColumn): string {
  const value = row.values[column.name]
  if (value === null || value === undefined) return '—'
  if (Array.isArray(value)) return value.join(' ')
  if (column.type === 'Number' && typeof value === 'number') return formatFull(value)
  if (column.type === 'Date' && typeof value === 'string') return formatDate(value)
  return String(value)
}

const expanded = ref<Set<string>>(new Set())
function toggle(key: string) {
  const next = new Set(expanded.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  expanded.value = next
}

const kindLabel = computed(() =>
  ({ msisdn: 'Number (MSISDN)', imsi: 'SIM (IMSI)', imei: 'IMEI' })[result.value?.kind ?? 'imsi'],
)
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-end gap-x-6 gap-y-3">
      <div v-if="rules.length > 1">
        <span :class="miniLabel">Rule</span>
        <div class="mt-1 inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" aria-label="Rule">
          <button
            v-for="r in rules"
            :key="r.rule"
            type="button"
            role="radio"
            :aria-checked="selected === r.rule"
            :class="segment(selected === r.rule)"
            @click="selected = r.rule"
          >
            {{ RULE_TITLES[r.rule] ?? r.rule }}
          </button>
        </div>
      </div>

      <div>
        <span :class="miniLabel">View</span>
        <div class="mt-1 inline-flex overflow-hidden rounded-[var(--radius-md)] border" role="radiogroup" aria-label="View">
          <button type="button" role="radio" :aria-checked="view === 'risk'" :class="segment(view === 'risk')" @click="setView('risk')">
            Risk
          </button>
          <button
            type="button"
            role="radio"
            :aria-checked="view === 'dataQuality'"
            :class="segment(view === 'dataQuality')"
            :disabled="!rule?.hasDataQualityView"
            :title="rule?.hasDataQualityView ? '' : 'An all-time count has no raw count and no share set aside.'"
            @click="setView('dataQuality')"
          >
            Data quality (not risk)
          </button>
        </div>
      </div>

      <form v-if="rule" class="flex flex-wrap items-end gap-2" @submit.prevent="apply">
        <label>
          <span :class="miniLabel">More than ({{ rule.unit }})</span>
          <input
            v-model.number="threshold"
            type="number"
            :min="rule.floor - 1"
            :class="[control, 'mt-1 w-24 tabular']"
            :aria-invalid="belowFloor"
          />
        </label>
        <label v-if="rule.rule === 'Randomisation20'">
          <span :class="miniLabel">and more than (TACs)</span>
          <input v-model.number="tacsThreshold" type="number" min="0" :class="[control, 'mt-1 w-24 tabular']" />
        </label>
        <Button type="submit" size="sm" variant="secondary" :disabled="belowFloor || list.isPending.value">Apply</Button>
        <Button
          v-if="threshold !== rule.threshold || tacsThreshold !== rule.tacsThreshold"
          type="button"
          size="sm"
          variant="ghost"
          @click="reset(); apply()"
        >
          Back to the configured value
        </Button>
      </form>

      <label v-if="hasDeviceType && deviceTypes.length">
        <span :class="miniLabel">{{ rule?.list === 'Sims' ? 'Device type (of its most frequent TAC, 20 days)' : 'Device type' }}</span>
        <select v-model="deviceType" :class="[control, 'mt-1']" @change="apply">
          <option value="">All device types</option>
          <option v-for="t in deviceTypes" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>

      <div class="ml-auto flex items-end gap-2">
        <label>
          <span :class="miniLabel">Rows</span>
          <select :value="pageSize" :class="[control, 'mt-1']" @change="changeSize(Number(($event.target as HTMLSelectElement).value))">
            <option :value="50">50</option>
            <option :value="100">100</option>
            <option :value="500">500</option>
          </select>
        </label>
        <Button
          v-if="canExport"
          type="button"
          size="sm"
          variant="secondary"
          :disabled="!result || result.total === 0 || exporter.isPending.value"
          @click="exportCsv"
        >
          {{ exporter.isPending.value ? 'Exporting…' : 'Export CSV' }}
        </Button>
      </div>
    </div>

    <p v-if="belowFloor && rule" class="text-xs text-[var(--c-danger-text)]" role="alert">
      Values below {{ rule.floor }} are not stored, so a threshold under {{ rule.floor - 1 }} cannot be listed here. The
      Explorer can count smaller values from the history.
    </p>

    <div v-if="rule" class="flex flex-col gap-1 text-xs text-[var(--c-text-secondary)]">
      <p>
        <span class="font-medium text-[var(--c-text)]">{{ RULE_TITLES[rule.rule] ?? rule.rule }}.</span>
        <template v-if="rule.windowFrom">
          Window {{ formatDate(rule.windowFrom) }} – {{ formatDate(rule.windowTo) }}, {{ rule.daysWithData }} of
          {{ rule.windowDays }} days {{ rule.daysWithData === 1 ? 'has' : 'have' }} data.
        </template>
        <template v-else> All time, from current state. At most an anomaly: it accumulates ordinary churn.</template>
        Configured threshold: {{ rule.threshold === null ? 'not calibrated' : `more than ${formatFull(rule.threshold)}` }};
        stored from {{ rule.floor }}.
      </p>
      <p
        v-if="rule.flaggedDays.length > 0"
        class="rounded-[var(--radius-md)] border border-[var(--c-warning)] bg-[var(--c-warning-subtle)] px-3 py-2 text-[var(--c-text)]"
        role="note"
      >
        {{ rule.flaggedDays.length }} of this window's days {{ rule.flaggedDays.length === 1 ? 'is' : 'are' }} flagged by the feed-quality monitor for
        {{ rule.cappingChecks.map((c) => CHECK_LABELS[c] ?? c).join(' or ') }}
        ({{ formatDate(rule.flaggedDays[0] ?? null) }} – {{ formatDate(rule.flaggedDays[rule.flaggedDays.length - 1] ?? null) }}).
        Rows matching those defects are set aside, and what remains is shown as an anomaly at most, never a risk signal.
      </p>
      <p v-if="view === 'dataQuality'" class="font-medium text-[var(--c-text)]">
        Not behaviour: these entities cross the threshold only through rows that match known feed defects, or most of their
        evidence was set aside.
      </p>
    </div>

    <section aria-label="Analysis" class="flex flex-col gap-2">
      <button
        type="button"
        class="w-fit text-xs font-medium text-[var(--c-accent)] hover:underline"
        :aria-expanded="showAnalysis"
        @click="showAnalysis = !showAnalysis"
      >
        {{ showAnalysis ? 'Hide the analysis' : 'Show the analysis' }}
      </button>
      <div v-if="showAnalysis" class="grid gap-3" :class="hasDeviceType || rule?.list === 'Numbers' ? 'xl:grid-cols-2' : ''">
        <div class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4">
          <h3 class="text-xs font-semibold text-[var(--c-text)]">Where the threshold sits</h3>
          <p class="mb-2 text-2xs text-[var(--c-text-muted)]">
            Every stored entity by its count, as of {{ distribution.data.value ? formatDate(distribution.data.value.asOf) : '…' }};
            values under {{ rule?.floor }} are not stored. The shaded range is what the list holds.
          </p>
          <RiskDistributionChart v-if="distribution.data.value" :data="distribution.data.value" />
          <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">
            {{ distribution.isError.value ? 'The distribution could not be loaded.' : 'Loading…' }}
          </p>
        </div>
        <div v-if="hasDeviceType" class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)]">
          <div class="px-4 pt-4">
            <h3 class="text-xs font-semibold text-[var(--c-text)]">What the list is made of</h3>
            <p class="text-2xs text-[var(--c-text-muted)]">
              Listed at the configured threshold, by GSMA device type of the
              {{ typeBreakdown.data.value?.basis ?? 'handset' }}. Choose one to narrow the list to it.
            </p>
          </div>
          <!-- Capped: with a dozen device types this card stretched the chart card beside it into a blank block. -->
          <div v-if="deviceRows.length" class="max-h-[20rem] overflow-y-auto">
            <DimensionTable
              :rows="deviceRows"
              header="Device type"
              :unit="rule?.family === 'Sim' ? 'SIMs' : 'IMEIs'"
              @select="filterTo"
            />
          </div>
          <p v-else class="px-4 py-10 text-center text-2xs text-[var(--c-text-muted)]">
            {{ typeBreakdown.isError.value ? 'Not available for this rule.' : typeBreakdown.isPending.value ? 'Loading…' : 'Nothing listed.' }}
          </p>
        </div>
        <div v-if="rule?.list === 'Numbers'" class="rounded-[var(--radius-lg)] border bg-[var(--c-surface)] p-4">
          <h3 class="text-xs font-semibold text-[var(--c-text)]">SIM changes per day</h3>
          <p class="mb-2 text-2xs text-[var(--c-text-muted)]">
            Numbers whose SIM changed, by day: counted, and set aside because the feed listed one of their SIMs under several
            numbers. Shaded days were flagged by the feed-quality monitor.
          </p>
          <RiskDailyChart v-if="daily.data.value" :days="daily.data.value.days" />
          <p v-else class="py-10 text-center text-2xs text-[var(--c-text-muted)]">Loading…</p>
        </div>
      </div>
    </section>

    <p v-if="error" class="rounded-[var(--radius-md)] border px-3 py-2 text-sm text-[var(--c-text-secondary)]" role="alert">
      {{ error }}
    </p>

    <div v-else class="overflow-hidden rounded-[var(--radius-lg)] border bg-[var(--c-surface)]">
      <div class="overflow-x-auto">
        <table class="w-full text-xs" :aria-busy="list.isPending.value">
          <thead class="bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-3 py-2 font-medium">{{ view === 'risk' ? 'Level' : 'Status' }}</th>
              <th scope="col" class="px-3 py-2 font-medium">{{ kindLabel }}</th>
              <th
                v-for="c in result?.columns ?? []"
                :key="c.name"
                scope="col"
                class="px-3 py-2 font-medium"
                :class="c.type === 'Number' ? 'text-right' : ''"
              >
                {{ c.label }}
              </th>
              <th scope="col" class="min-w-[28rem] px-3 py-2 font-medium">Reasons</th>
            </tr>
          </thead>
          <tbody>
            <tr v-if="result && result.rows.length === 0">
              <td :colspan="(result.columns.length ?? 0) + 3" class="px-3 py-8 text-center text-[var(--c-text-muted)]">
                Nothing is over this threshold in this view.
              </td>
            </tr>
            <tr v-for="row in result?.rows ?? []" :key="row.key" class="border-t align-top">
              <td class="px-3 py-2">
                <RiskLevelBadge v-if="view === 'risk'" :level="row.level" compact />
                <span v-else class="text-2xs text-[var(--c-text-secondary)]">
                  {{ row.assessable ? 'Over only before screens' : 'Mostly set aside' }}
                </span>
              </td>
              <td class="px-3 py-2">
                <button
                  v-if="row.drillable"
                  type="button"
                  class="font-mono tabular text-[var(--c-accent)] hover:underline"
                  @click="emit('drill', row.key)"
                >
                  {{ row.key }}
                </button>
                <span v-else class="font-mono tabular" title="Masked: opening it needs identifier.reveal">{{ row.key }}</span>
              </td>
              <td
                v-for="c in result?.columns ?? []"
                :key="c.name"
                class="px-3 py-2 whitespace-nowrap"
                :class="c.type === 'Number' ? 'tabular text-right' : c.type === 'Tags' ? 'font-mono' : ''"
              >
                {{ cell(row, c) }}
              </td>
              <td class="px-3 py-2 text-[var(--c-text-secondary)]">
                <p>{{ row.reasons[0]?.text }}</p>
                <template v-if="row.reasons.length > 1">
                  <ul v-if="expanded.has(row.key)" class="mt-1 flex flex-col gap-1">
                    <li v-for="r in row.reasons.slice(1)" :key="r.rule">{{ r.text }}</li>
                  </ul>
                  <button type="button" class="mt-1 text-2xs text-[var(--c-accent)] hover:underline" @click="toggle(row.key)">
                    {{ expanded.has(row.key) ? 'Fewer reasons' : `${row.reasons.length - 1} more` }}
                  </button>
                </template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <Pagination
        v-if="result"
        :page="result.page"
        :page-size="result.pageSize"
        :total="result.reachable"
        :loading="list.isPending.value"
        @update:page="goTo"
      />
      <p v-if="result" class="border-t px-4 py-2 text-2xs text-[var(--c-text-muted)]">
        {{ formatFull(result.total) }} listed<template v-if="result.total > result.reachable">, the first
        {{ formatFull(result.reachable) }} reachable</template>. Threshold: more than {{ formatFull(result.threshold)
        }}<template v-if="result.tacsThreshold !== null"> and {{ formatFull(result.tacsThreshold) }} TACs</template
        ><template v-if="result.overridden"> (for this view only)</template>. Rules {{ result.ruleSetVersion }}, as of
        {{ formatDate(result.asOf) }}.<template v-if="result.masked"> Identifiers are masked.</template>
      </p>
    </div>
  </div>
</template>
