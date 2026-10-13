<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import { RULE_TITLES, useRiskOverview } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import BarChart from '@/design-system/BarChart.vue'
import RiskLevelBadge from '@/features/risk/RiskLevelBadge.vue'
import { useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull } from '@/lib/format'
import type { RiskOverviewConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { useKept } from '../overview/shared/useKept'

/**
 * How many entities each rule lists at its configured threshold, at what level, and how many more
 * crossed it only through feed defects. Counts only - it names nobody, so it needs risk.view and
 * nothing more; opening a list is the risk page's job, behind the lookup permissions.
 */
const props = defineProps<WidgetViewProps<RiskOverviewConfig>>()
type Snapshot = SnapshotData<'risk.overview'>

const { active, status, ready } = useRiskSource(props)
const query = useRiskOverview(() => active.value && ready.value)
const overview = useWidgetData(props, query, (raw) => (raw as Snapshot).overview ?? undefined)
const { value: data, loading, updating } = useKept(overview)

const rules = computed(() => {
  const wanted = new Set<string>(props.instance.config.rules)
  return (status.data.value?.rules ?? []).filter((r) => wanted.has(r.rule))
})
const counts = computed(() => new Map((data.value?.rules ?? []).map((c) => [c.rule, c])))

const bars = computed(() => {
  const rows = rules.value.map((r) => ({ key: RULE_TITLES[r.rule] ?? r.rule, count: counts.value.get(r.rule)?.listed ?? 0 }))
  const total = rows.reduce((sum, r) => sum + r.count, 0) || 1
  return rows.map((r) => ({ ...r, percent: (100 * r.count) / total }))
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Entities listed by each rule at its configured threshold<template v-if="data">, as of {{ formatDate(data.asOf) }}</template>.
    </p>
    <RiskNotice :status="status.data.value" />
    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value || loading"
      :is-error="status.isError.value || overview.isError.value"
      :error="status.error.value ?? overview.error.value"
      min-height="8rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : overview.refetch()"
    >
      <div v-if="instance.config.display === 'bar'" class="relative min-h-0 flex-1">
        <BarChart :data="bars" unit="entities listed" height="auto" :selectable="false" />
      </div>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">Rule</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Listed</th>
              <th scope="col" class="px-2 py-1.5 font-medium">Level</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Data quality (not risk)</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in rules" :key="r.rule" class="border-t">
              <td class="px-2 py-1.5">{{ RULE_TITLES[r.rule] ?? r.rule }}</td>
              <template v-if="counts.get(r.rule)">
                <td class="tabular px-2 py-1.5 text-right font-medium">{{ formatFull(counts.get(r.rule)!.listed) }}</td>
                <td class="px-2 py-1.5"><RiskLevelBadge :level="counts.get(r.rule)!.level" :capped="counts.get(r.rule)!.capped" compact /></td>
                <td class="tabular px-2 py-1.5 text-right text-[var(--c-text-secondary)]">{{ formatFull(counts.get(r.rule)!.dataQuality) }}</td>
              </template>
              <td v-else colspan="3" class="px-2 py-1.5 text-[var(--c-text-muted)]">
                {{ r.threshold === null ? 'Not calibrated: nothing is judged.' : 'Not available.' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      Observed, not proven.
      <RouterLink v-if="mode === 'view'" to="/risk" class="text-[var(--c-accent)] hover:underline">Open the lists →</RouterLink>
    </p>
  </div>
</template>
