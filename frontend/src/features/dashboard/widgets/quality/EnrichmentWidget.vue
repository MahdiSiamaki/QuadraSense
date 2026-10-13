<script setup lang="ts">
import { computed } from 'vue'
import { useKpiSummary } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import { formatFull, formatPercent } from '@/lib/format'
import type { EnrichmentConfig } from './config'
import { unregisteredTacBindings } from '../overview/kpiMetrics'
import { lensFilters } from '../overview/shared/filters'
import { useKept } from '../overview/shared/useKept'

/**
 * Every active binding, accounted for: enriched from GSMA, the unknown-device sentinel, malformed
 * IMEIs, and well-formed IMEIs whose TAC GSMA does not know. The four sum exactly to the total -
 * shown together because only one of them is a defect.
 *
 * Unregistered TAC is computed from the exact counts, never from the coverage percentage: deriving it
 * from a figure rounded to three decimals put it about 30,000 rows off the true value, on the card
 * whose whole job is to report data quality accurately.
 */
const props = defineProps<WidgetViewProps<EnrichmentConfig>>()

const active = useQueryActive(props)
const filters = computed(() => lensFilters(props.lens))
const query = useKpiSummary(filters, { enabled: active })
const kpi = useWidgetData(props, query, (raw) => (raw as SnapshotData<'quality.enrichment'>).kpi)
const { value: k, loading, updating } = useKept(kpi)

const rows = computed(() => {
  const s = k.value
  if (!s) return []
  return [
    { key: 'enriched', label: 'Enriched from GSMA', value: s.tacMatchedBindings, tone: 'text-[var(--c-success-text)]', note: `${formatPercent(s.tacCoveragePercent, 2)} of active bindings.` },
    { key: 'unknown', label: 'Unknown device (000000)', value: s.unknownDeviceBindings, tone: 'text-[var(--c-warning-text)]', note: 'A sentinel the source uses for “device not known”. Expected, not a defect.' },
    { key: 'malformed', label: 'Malformed IMEI', value: s.malformedImeiBindings, tone: 'text-[var(--c-danger-text)]', note: 'Numeric but not 14 digits. The only genuine defect here — worth raising with the source.' },
    { key: 'unregistered', label: 'Unregistered TAC', value: unregisteredTacBindings(s), tone: '', note: 'Well-formed IMEI whose TAC is absent from the GSMA database.' },
  ]
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <p v-if="instance.subtitle === null" class="text-xs text-[var(--c-text-muted)]">
      Every active binding, accounted for. These are characteristics of the feed, not defects — except where noted.
    </p>
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="kpi.isError.value"
      :error="kpi.error.value"
      min-height="5rem"
      gap="none"
      @retry="kpi.refetch()"
    >
      <dl v-if="instance.config.display === 'figures'" class="grid min-h-0 flex-1 content-start gap-x-8 gap-y-4 overflow-auto sm:grid-cols-2 lg:grid-cols-4">
        <div v-for="r in rows" :key="r.key">
          <dt class="text-xs text-[var(--c-text-muted)]">{{ r.label }}</dt>
          <dd class="mt-1 text-lg font-semibold tabular" :class="r.tone">{{ formatFull(r.value) }}</dd>
          <dd class="mt-0.5 text-xs text-[var(--c-text-secondary)]">{{ r.note }}</dd>
        </div>
      </dl>
      <div v-else class="min-h-0 flex-1 overflow-auto overscroll-contain">
        <table class="w-full text-xs">
          <thead class="sticky top-0 bg-[var(--c-surface-sunken)] text-left text-2xs text-[var(--c-text-muted)]">
            <tr>
              <th scope="col" class="px-2 py-1.5 font-medium">Category</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Bindings</th>
              <th scope="col" class="px-2 py-1.5 text-right font-medium">Share</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in rows" :key="r.key" class="border-t align-top">
              <th scope="row" class="px-2 py-1.5 text-left font-normal">
                <span class="font-medium">{{ r.label }}</span>
                <span class="block text-2xs text-[var(--c-text-muted)]">{{ r.note }}</span>
              </th>
              <td class="tabular px-2 py-1.5 text-right">{{ formatFull(r.value) }}</td>
              <td class="tabular px-2 py-1.5 text-right">{{ k?.activeBindings ? formatPercent((100 * r.value) / k.activeBindings, 2) : '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </AsyncBoundary>
    <p v-if="k" class="shrink-0 border-t pt-2 text-xs text-[var(--c-text-muted)]">
      The four figures sum to <span class="tabular font-medium text-[var(--c-text-secondary)]">{{ formatFull(k.activeBindings) }}</span>
      active bindings. The enrichment ceiling is set by unknown-device rows, not by gaps in the GSMA database.
    </p>
  </div>
</template>
