<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ApiError, saveFile } from '@/api/client'
import { RULE_TITLES, useRiskExport, useRiskList, type RiskColumn, type RiskListRequest, type RiskRow } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import Button from '@/design-system/Button.vue'
import Pagination from '@/design-system/Pagination.vue'
import { Permission, useAuth } from '@/features/auth/useAuth'
import RiskLevelBadge from '@/features/risk/RiskLevelBadge.vue'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { RiskListConfig } from './config'
import EntityDialog from './EntityDialog.vue'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { thresholdProblem } from './threshold'

/**
 * Who a rule lists - a list of identifiers, so it runs only when a person presses Run, and every run
 * is the risk list endpoint's own audited request (ADR-016 §11). It never runs by itself: not on
 * mount, not on the dashboard's Refresh, not in an export.
 *
 * The widget's permissions are the rule's lookup permission as well as risk.view (the host checks
 * them before mounting); the server checks them again. A threshold override is sent for this run
 * only and audited with it; the configured threshold is never changed from here.
 */
const props = defineProps<WidgetViewProps<RiskListConfig>>()

const { can } = useAuth()
const { status, ready } = useRiskSource(props)
const config = computed(() => props.instance.config)
const rule = computed(() => status.data.value?.rules.find((r) => r.rule === config.value.rule) ?? null)

const list = useRiskList()
const exporter = useRiskExport()
const page = ref(1)
const result = computed(() => list.data.value ?? null)

const problem = computed(() => (rule.value ? thresholdProblem(rule.value, config.value.thresholdOverride) : null))
const canRun = computed(() => ready.value && rule.value !== null && problem.value === null && props.mode !== 'export')

function request(): RiskListRequest {
  const r = rule.value!
  const hasDeviceType = r.list !== 'Numbers'
  return {
    rule: r.rule,
    view: r.hasDataQualityView ? config.value.view : 'risk',
    threshold: config.value.thresholdOverride,
    tacsThreshold: r.rule === 'Randomisation20' ? config.value.tacsThresholdOverride : null,
    deviceTypes: hasDeviceType ? config.value.deviceTypes : [],
  }
}

function run(toPage = 1) {
  if (!canRun.value) return
  page.value = toPage
  list.mutate({ ...request(), page: toPage, pageSize: config.value.pageSize })
}

// A result is only true of the settings it ran with: a change clears it rather than leaving a list
// on screen under a heading that no longer describes it.
watch(
  () => JSON.stringify(config.value),
  () => {
    list.reset()
    page.value = 1
  },
)

async function exportCsv() {
  if (!canRun.value) return
  const file = await exporter.mutateAsync(request())
  saveFile(file, `risk-${config.value.rule}-${request().view}.csv`)
}

const error = computed(() => {
  const e = list.error.value ?? exporter.error.value
  if (!e) return null
  return e instanceof ApiError ? (e.problem?.detail ?? e.problem?.title ?? e.message) : 'The list could not be loaded.'
})

/** The first three measures: a card is narrower than the risk page, and the reason carries the rest. */
const columns = computed<RiskColumn[]>(() => (result.value?.columns ?? []).slice(0, 3))

function cell(row: RiskRow, column: RiskColumn): string {
  const value = row.values[column.name]
  if (value === null || value === undefined) return '—'
  if (Array.isArray(value)) return value.join(' ')
  if (column.type === 'Number' && typeof value === 'number') return formatFull(value)
  if (column.type === 'Date' && typeof value === 'string') return formatDate(value)
  return String(value)
}

const kindLabel = computed(() => ({ msisdn: 'Number (MSISDN)', imsi: 'SIM (IMSI)', imei: 'IMEI' })[result.value?.kind ?? 'imsi'])
const opened = ref<string | null>(null)

const settingsLine = computed(() => {
  const r = rule.value
  if (!r) return ''
  const t = config.value.thresholdOverride
  const parts = [
    t === null
      ? r.threshold === null
        ? 'not calibrated'
        : `more than ${formatFull(r.threshold)} (configured)`
      : `more than ${formatFull(t)} (for this widget only; configured ${r.threshold === null ? 'none' : formatFull(r.threshold)})`,
  ]
  if (config.value.view === 'dataQuality' && r.hasDataQualityView) parts.push('data-quality view (not risk)')
  if (config.value.deviceTypes.length && r.list !== 'Numbers') parts.push(config.value.deviceTypes.join(', '))
  return parts.join(' · ')
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <RiskNotice :status="status.data.value" />

    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value"
      :is-error="status.isError.value"
      :error="status.error.value"
      min-height="8rem"
      gap="none"
      @retry="status.refetch()"
    >
      <div class="flex flex-wrap items-start justify-between gap-2">
        <p class="min-w-0 text-xs text-[var(--c-text-secondary)]">
          <span class="font-medium text-[var(--c-text)]">{{ RULE_TITLES[config.rule] ?? config.rule }}</span>
          <template v-if="rule">: {{ settingsLine }}.</template>
        </p>
        <div class="flex shrink-0 items-center gap-2">
          <Button
            v-if="result && can(Permission.DataExport)"
            type="button"
            size="sm"
            variant="ghost"
            :disabled="result.total === 0 || exporter.isPending.value"
            @click="exportCsv"
          >
            {{ exporter.isPending.value ? 'Exporting…' : 'Export CSV' }}
          </Button>
          <Button type="button" size="sm" :disabled="!canRun || list.isPending.value" @click="run(1)">
            {{ list.isPending.value ? 'Running…' : result ? 'Run again' : 'Run' }}
          </Button>
        </div>
      </div>

      <p v-if="problem" class="text-xs text-[var(--c-danger-text)]" role="alert">{{ problem }}</p>
      <p v-else-if="error" class="rounded-[var(--radius-md)] border px-3 py-2 text-xs text-[var(--c-text-secondary)]" role="alert">
        {{ error }}
      </p>
      <p v-else-if="!result && !list.isPending.value" class="py-6 text-center text-xs text-[var(--c-text-muted)]">
        Lists identifiers, so it runs only when you press Run. Each run is audited.
      </p>

      <div v-if="result" class="flex min-h-0 flex-1 flex-col overflow-hidden rounded-[var(--radius-md)] border">
        <div class="min-h-0 flex-1 overflow-auto overscroll-contain">
          <table class="w-full text-xs" :aria-busy="list.isPending.value">
            <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
              <tr>
                <th scope="col" class="px-2 py-1.5 font-medium">{{ result.view === 'risk' ? 'Level' : 'Status' }}</th>
                <th scope="col" class="px-2 py-1.5 font-medium">{{ kindLabel }}</th>
                <th v-for="c in columns" :key="c.name" scope="col" class="px-2 py-1.5 font-medium" :class="c.type === 'Number' ? 'text-right' : ''">
                  {{ c.label }}
                </th>
                <th scope="col" class="min-w-[16rem] px-2 py-1.5 font-medium">Reason</th>
              </tr>
            </thead>
            <tbody>
              <tr v-if="result.rows.length === 0">
                <td :colspan="columns.length + 3" class="px-2 py-6 text-center text-[var(--c-text-muted)]">Nothing is over this threshold in this view.</td>
              </tr>
              <tr v-for="row in result.rows" :key="row.key" class="border-t align-top">
                <td class="px-2 py-1.5">
                  <RiskLevelBadge v-if="result.view === 'risk'" :level="row.level" compact />
                  <span v-else class="text-2xs text-[var(--c-text-secondary)]">{{ row.assessable ? 'Over only before screens' : 'Mostly set aside' }}</span>
                </td>
                <td class="px-2 py-1.5">
                  <button
                    v-if="row.drillable && mode === 'view'"
                    type="button"
                    class="font-mono tabular text-[var(--c-accent)] hover:underline"
                    @click="opened = row.key"
                  >
                    {{ row.key }}
                  </button>
                  <span v-else class="font-mono tabular" :title="row.drillable ? undefined : 'Masked: opening it needs identifier.reveal'">{{ row.key }}</span>
                </td>
                <td v-for="c in columns" :key="c.name" class="px-2 py-1.5 whitespace-nowrap" :class="c.type === 'Number' ? 'tabular text-right' : ''">
                  {{ cell(row, c) }}
                </td>
                <td class="px-2 py-1.5 text-[var(--c-text-secondary)]">
                  <p class="line-clamp-2" :title="row.reasons.map((r) => r.text).join('\n')">{{ row.reasons[0]?.text }}</p>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <Pagination
          :page="result.page"
          :page-size="result.pageSize"
          :total="result.reachable"
          :loading="list.isPending.value"
          @update:page="run"
        />
      </div>

      <p v-if="result" class="text-2xs text-[var(--c-text-muted)]">
        {{ formatFull(result.total) }} listed<template v-if="result.total > result.reachable">, the first {{ formatFull(result.reachable) }} reachable</template>.
        Threshold: more than {{ formatFull(result.threshold) }}<template v-if="result.tacsThreshold !== null"> and {{ formatFull(result.tacsThreshold) }} TACs</template><template v-if="result.overridden"> (for this widget only)</template>.
        Rules {{ result.ruleSetVersion }}, as of {{ formatDate(result.asOf) }}.<template v-if="result.masked"> Identifiers are masked.</template>
        Observed, not proven.
      </p>
    </AsyncBoundary>

    <EntityDialog :identifier="opened" :data-through="status.data.value?.dataThrough ?? null" @close="opened = null" />
  </div>
</template>
