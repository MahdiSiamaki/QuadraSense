<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { useKpiSummary } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { useFreshness, useWorkerHealth } from '@/features/imports/useImportQueries'
import { useTacVersions } from '@/features/imports/useTacVersions'
import { formatDate, formatDateTime, formatRelative } from '@/lib/format'
import type { FreshnessConfig } from './config'
import { summariseMissing } from './missingRuns'

/**
 * How current the dashboard's data is - the old page's top card, unchanged in what it says.
 *
 * Every figure on a dashboard is only as trustworthy as this answer: a vendor share of 41.8% means one
 * thing if it describes yesterday and something else if the last import was six weeks ago. Three
 * facts, in the order they change a reader's interpretation: how recent the data is, which days are
 * missing, and which TAC mapping the names come from - and, optionally, the pipeline.
 *
 * The figures and the day named here come from two places, the KPI marts and the import history.
 * During a mart rebuild they disagree - the dashboard serves the newest delivery whose marts are
 * complete - and the card says so rather than show one delivery's numbers under another's date. The
 * KPI is deliberately the unfiltered one, spelled as "no filter" is everywhere, so it shares one
 * cache entry with the page.
 */
const props = defineProps<WidgetViewProps<FreshnessConfig>>()
type Snapshot = SnapshotData<'ops.freshness'>

const config = computed(() => props.instance.config)
const active = useQueryActive(props)

const freshnessQuery = useFreshness({ enabled: active })
const healthQuery = useWorkerHealth({ enabled: () => active.value && config.value.showPipeline })
const tacQuery = useTacVersions({ enabled: () => active.value && config.value.showTac })
const kpiQuery = useKpiSummary(() => ({ includeUnknownDevice: true }), { enabled: active })

const freshness = useWidgetData(props, freshnessQuery, (raw) => (raw as Snapshot).freshness)
const health = useWidgetData(props, healthQuery, (raw) => (raw as Snapshot).workerHealth)
const tacVersions = useWidgetData(props, tacQuery, (raw) => (raw as Snapshot).tacVersions)
const kpi = useWidgetData(props, kpiQuery, (raw) => (raw as Snapshot).kpi ?? undefined)

const sqm = computed(() => freshness.data.value?.find((f) => f.sourceCode === 'SQM') ?? null)
const activeTac = computed(() => tacVersions.data.value?.find((v) => v.status === 'Active') ?? null)
const pendingTac = computed(() => tacVersions.data.value?.filter((v) => v.status === 'Ready') ?? [])

const martBehind = computed(() => {
  const served = kpi.data.value?.deliveryDate ?? null
  const imported = sqm.value?.latestBusinessDate ?? null
  if (!imported || !kpi.data.value || served === imported) return null
  return { served, imported }
})

/**
 * How to read the days-behind figure. From the one delivery pattern observed: files arrive about four
 * weeks behind the day they describe. 35 days is that lag plus a week; past 45 the delivery has
 * stopped rather than slipped. Stated here rather than hidden in a colour.
 */
const lagTone = computed(() => {
  const days = sqm.value?.daysBehind
  if (days === null || days === undefined) return 'var(--c-text-muted)'
  if (days > 45) return 'var(--c-danger-text)'
  if (days > 35) return 'var(--c-warning-text)'
  return 'var(--c-success-text)'
})

const RUNS_SHOWN = 4
const showAllRuns = ref(false)
const missing = computed(() => summariseMissing(sqm.value?.missingBusinessDates ?? []))
const visibleRuns = computed(() => (showAllRuns.value ? missing.value?.labels : missing.value?.labels.slice(0, RUNS_SHOWN)) ?? [])
const hiddenRuns = computed(() => Math.max(0, (missing.value?.labels.length ?? 0) - RUNS_SHOWN))
const link = computed(() => props.mode === 'view')
</script>

<template>
  <AsyncBoundary
    class="h-full"
    :is-loading="freshness.isPending.value"
    :is-error="freshness.isError.value"
    :error="freshness.error.value"
    min-height="4rem"
    gap="none"
    @retry="freshness.refetch()"
  >
    <dl class="grid h-full content-start gap-x-8 gap-y-4 overflow-auto sm:grid-cols-2 lg:grid-cols-4">
      <div>
        <dt class="text-xs text-[var(--c-text-muted)]">Data through</dt>
        <dd class="tabular mt-1 text-lg font-semibold tracking-tight">{{ formatDate(sqm?.latestBusinessDate ?? null) }}</dd>
        <dd class="mt-0.5 text-xs" :style="{ color: lagTone }">
          <template v-if="sqm?.daysBehind !== null && sqm?.daysBehind !== undefined">{{ sqm.daysBehind }} days behind today</template>
          <template v-else>no successful import yet</template>
        </dd>
        <dd v-if="martBehind" class="mt-1 text-xs text-[var(--c-warning-text)]" title="The marts are being rebuilt. Figures are from the last complete delivery.">
          figures are from {{ martBehind.served ? formatDate(martBehind.served) : 'the initial dump' }}
        </dd>
        <dd v-if="sqm?.dashboardPendingSince" class="mt-1 text-xs text-[var(--c-warning-text)]">
          figures update once the queued files have landed
          <span class="text-[var(--c-text-muted)]">(pending since {{ formatDateTime(sqm.dashboardPendingSince) }})</span>
        </dd>
      </div>

      <div>
        <dt class="text-xs text-[var(--c-text-muted)]">Days not delivered</dt>
        <dd class="tabular mt-1 text-lg font-semibold tracking-tight" :style="{ color: missing ? 'var(--c-warning-text)' : 'var(--c-success-text)' }">
          {{ missing?.count ?? 0 }}
        </dd>
        <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
          <template v-if="missing">
            {{ visibleRuns.join(', ') }}<template v-if="missing.year">, {{ missing.year }}</template><template v-if="hiddenRuns && !showAllRuns">
              and
              <button type="button" class="font-medium text-[var(--c-accent)] hover:underline" @click="showAllRuns = true">
                {{ hiddenRuns }} more {{ hiddenRuns === 1 ? 'period' : 'periods' }}
              </button></template>. Charts show gaps, never interpolated points.
          </template>
          <template v-else>Every expected day is present.</template>
        </dd>
      </div>

      <div v-if="config.showTac">
        <dt class="text-xs text-[var(--c-text-muted)]">Device database</dt>
        <dd class="mt-1 text-lg font-semibold tracking-tight">{{ activeTac?.versionLabel ?? (tacVersions.isPending.value ? '…' : 'none active') }}</dd>
        <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
          <template v-if="pendingTac.length">
            <component :is="link ? RouterLink : 'span'" :to="link ? '/imports' : undefined" class="font-medium text-[var(--c-warning-text)]" :class="link ? 'hover:underline' : ''">
              {{ pendingTac.length }} {{ pendingTac.length === 1 ? 'version' : 'versions' }} waiting for activation
            </component>
          </template>
          <template v-else-if="activeTac?.activatedAt">
            activated <span :title="formatDateTime(activeTac.activatedAt)">{{ formatRelative(activeTac.activatedAt) }}</span>
          </template>
          <template v-else>GSMA manufacturer and model mapping.</template>
        </dd>
      </div>

      <div v-if="config.showPipeline">
        <dt class="text-xs text-[var(--c-text-muted)]">Import pipeline</dt>
        <dd class="mt-1 text-lg font-semibold tracking-tight" :style="{ color: (sqm?.failedLast7Days ?? 0) > 0 ? 'var(--c-danger-text)' : 'var(--c-text)' }">
          <template v-if="(health.data.value?.running ?? 0) > 0">{{ health.data.value?.running }} running</template>
          <template v-else-if="(sqm?.failedLast7Days ?? 0) > 0">{{ sqm?.failedLast7Days }} failed</template>
          <template v-else>idle</template>
        </dd>
        <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">
          <component :is="link ? RouterLink : 'span'" :to="link ? '/imports' : undefined" :class="link ? 'hover:text-[var(--c-text)] hover:underline' : ''">
            {{ health.data.value?.queued ?? 0 }} queued<template v-if="link"> · open the Import Center</template>
          </component>
        </dd>
      </div>
    </dl>
  </AsyncBoundary>
</template>
