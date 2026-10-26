<script setup lang="ts">
import { computed } from 'vue'
import { useKpiSummary } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { KpiConfig } from './config'
import { metricQualifier, metricSpec } from './kpiMetrics'
import { lensFilters } from './shared/filters'
import { useKept } from './shared/useKept'
import { useRefresh } from './shared/useRefresh'

/**
 * One headline figure: the widget is itself a card (chrome `bare`), a KpiCard with the metric's
 * identifier, qualifier and definition.
 *
 * Every KPI widget and every KPI summary on a dashboard builds its filters with `lensFilters`, so
 * those with equal lenses are one /kpi request.
 */
const props = defineProps<WidgetViewProps<KpiConfig>>()

const active = useQueryActive(props)
const filters = computed(() => lensFilters(props.lens))
const query = useKpiSummary(filters, { enabled: active })
const kpi = useWidgetData(props, query, (raw) => (raw as SnapshotData<'metric.kpi'>).kpi)
const { value: summary, loading, updating } = useKept(kpi)
useRefresh([{ enabled: () => active.value, refetch: query.refetch }])

const config = computed(() => props.instance.config)
const spec = computed(() => metricSpec(config.value.metric))
const precision = computed(() => config.value.precision ?? 1)

const value = computed(() => (summary.value ? spec.value.value(summary.value) : undefined))
const qualifier = computed(() =>
  config.value.showQualifier === false ? undefined : metricQualifier(spec.value, summary.value, precision.value),
)
const definition = computed(() => (config.value.showDefinition === false ? undefined : spec.value.definition))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <AsyncBoundary
      class="min-h-0 flex-1"
      :is-loading="loading"
      :is-error="kpi.isError.value"
      :error="kpi.error.value"
      min-height="5.5rem"
      gap="none"
      @retry="kpi.refetch()"
    >
      <template #skeleton>
        <div class="h-[5.5rem] rounded-[var(--radius-lg)] bg-[var(--c-surface-sunken)]" />
      </template>

      <KpiCard
        class="h-full transition-opacity duration-150"
        :class="updating ? 'opacity-70' : ''"
        :label="instance.title ?? spec.label"
        :value="value"
        :identifier="spec.identifier"
        :qualifier="qualifier"
        :definition="definition"
        :percent="spec.percent"
        :tone="spec.tone"
        :format="config.numberFormat"
        :precision="spec.percent ? precision : undefined"
      />
    </AsyncBoundary>
  </div>
</template>
