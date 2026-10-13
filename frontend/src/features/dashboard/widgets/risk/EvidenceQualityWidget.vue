<script setup lang="ts">
import { computed } from 'vue'
import { CHECK_LABELS, RULE_TITLES, useRiskOverview } from '@/api/risk'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatDate, formatFull, formatPercent } from '@/lib/format'
import type { EvidenceQualityConfig } from './config'
import RiskNotice from './RiskNotice.vue'
import { useRiskSource } from './shared'
import { useKept } from '../overview/shared/useKept'

/**
 * How much of each rule's evidence is clean: entities over the threshold on clean evidence, against
 * those over it only through feed defects (listed as data quality, never as risk), and the flagged
 * days in the rule's window that hold it at anomaly. The figures behind "observed, not proven".
 */
const props = defineProps<WidgetViewProps<EvidenceQualityConfig>>()
type Snapshot = SnapshotData<'risk.evidenceQuality'>

const { active, status, ready } = useRiskSource(props)
const query = useRiskOverview(() => active.value && ready.value)
const overview = useWidgetData(props, query, (raw) => (raw as Snapshot).overview ?? undefined)
const { value: data, loading, updating } = useKept(overview)

const rows = computed(() => {
  const wanted = new Set<string>(props.instance.config.rules)
  const counts = new Map((data.value?.rules ?? []).map((c) => [c.rule, c]))
  return (status.data.value?.rules ?? [])
    .filter((r) => wanted.has(r.rule))
    .map((r) => {
      const c = counts.get(r.rule)
      const over = c ? c.listed + c.dataQuality : null
      return {
        rule: r.rule,
        listed: c?.listed ?? null,
        dataQuality: c?.dataQuality ?? null,
        defectShare: over ? ((c?.dataQuality ?? 0) / over) * 100 : null,
        hasView: r.hasDataQualityView,
        capped: c?.capped ?? false,
        flaggedDays: r.flaggedDays.length,
        windowDays: r.windowDays,
        checks: r.cappingChecks.map((x) => CHECK_LABELS[x] ?? x),
      }
    })
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Over the threshold on clean evidence, against over it only through feed defects<template v-if="data">, as of
      {{ formatDate(data.asOf) }}</template>.
    </p>
    <RiskNotice :status="status.data.value" />
    <AsyncBoundary
      v-if="!status.data.value || ready"
      class="min-h-0 flex-1"
      :is-loading="status.isPending.value || loading"
      :is-error="status.isError.value || overview.isError.value"
      :error="status.error.value ?? overview.error.value"
      min-height="6rem"
      gap="none"
      @retry="status.isError.value ? status.refetch() : overview.refetch()"
    >
      <div class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">Rule</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Clean evidence</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Feed defects only</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Share set aside</th>
              <th scope="col" class="px-2 py-1.5 font-medium">Flagged days in the window</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in rows" :key="r.rule" class="border-t align-top">
              <td class="px-2 py-1.5">{{ RULE_TITLES[r.rule] ?? r.rule }}</td>
              <td class="tabular px-2 py-1.5 text-right">{{ r.listed === null ? '—' : formatFull(r.listed) }}</td>
              <td class="tabular px-2 py-1.5 text-right">
                <template v-if="!r.hasView"><span class="text-[var(--c-text-muted)]" title="An all-time count has no raw count and no share set aside.">n/a</span></template>
                <template v-else>{{ r.dataQuality === null ? '—' : formatFull(r.dataQuality) }}</template>
              </td>
              <td class="tabular px-2 py-1.5 text-right">
                {{ r.hasView && r.defectShare !== null ? formatPercent(r.defectShare) : '—' }}
              </td>
              <td class="px-2 py-1.5 text-2xs text-[var(--c-text-secondary)]">
                <template v-if="r.flaggedDays > 0">
                  {{ r.flaggedDays }} of {{ r.windowDays }} ({{ r.checks.join(', ') }}) - held at anomaly
                </template>
                <template v-else-if="r.checks.length">None</template>
                <template v-else>—</template>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>
    <p class="shrink-0 text-2xs text-[var(--c-text-muted)]">
      Feed defects only: entities that cross the threshold only through rows matching known feed defects, or whose
      evidence was mostly set aside. They are listed under data quality, not as risk.
    </p>
  </div>
</template>
