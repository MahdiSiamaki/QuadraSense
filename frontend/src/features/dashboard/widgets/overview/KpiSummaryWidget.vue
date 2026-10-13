<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useKpiSummary } from '@/api/dashboard'
import AsyncBoundary from '@/design-system/AsyncBoundary.vue'
import KpiCard from '@/design-system/KpiCard.vue'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { SnapshotData } from '@/features/dashboard/types/snapshot'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'
import type { KpiSummaryConfig } from './config'
import { KPI_METRICS, metricQualifier, metricSpec } from './kpiMetrics'
import { kpiRowColumns } from './kpiRow'
import { lensFilters } from './shared/filters'
import { useKept } from './shared/useKept'
import { useRefresh } from './shared/useRefresh'

/**
 * Several headline figures from one /kpi response: the dashboard page's KPI row, as one widget.
 *
 * The widget is a row of cards, so it has no card of its own (chrome `bare`), and it is as tall as
 * its cards (`height: 'content'`): a figure read at a glance is never behind a scrollbar. The cards
 * sit in one row when the width allows, otherwise in even rows of three or two - never five and a
 * stray one (`kpiRow.ts`), as the dashboard page's own KPI row did.
 *
 * The query is the same `/kpi` request, with the same filters, as `metric.kpi`: a summary and single
 * KPIs on one dashboard under one lens are one request.
 */
const props = defineProps<WidgetViewProps<KpiSummaryConfig>>()

const active = useQueryActive(props)
const filters = computed(() => lensFilters(props.lens))
const query = useKpiSummary(filters, { enabled: active })
const kpi = useWidgetData(props, query, (raw) => (raw as SnapshotData<'metric.kpiSummary'>).kpi)
const { value: summary, loading, updating } = useKept(kpi)
useRefresh([{ enabled: () => active.value, refetch: query.refetch }])

const config = computed(() => props.instance.config)
const precision = computed(() => config.value.precision ?? 1)

/** The chosen metrics in the order chosen, unknown values dropped, each once. */
const cards = computed(() =>
  [...new Set(config.value.metrics ?? [])]
    .filter((m) => (KPI_METRICS as readonly string[]).includes(m))
    .map((metric) => {
      const spec = metricSpec(metric)
      const k = summary.value
      return {
        metric,
        spec,
        value: k ? spec.value(k) : undefined,
        qualifier: metricQualifier(spec, k, precision.value),
        definition: config.value.showDefinition === false ? undefined : spec.definition,
      }
    }),
)

/** The row's width decides the columns; it is the widget's, not the window's. */
const row = ref<HTMLElement | null>(null)
const width = ref(0)
let resize: ResizeObserver | null = null
onMounted(() => {
  resize = new ResizeObserver(([entry]) => {
    if (entry) width.value = entry.contentRect.width
  })
  if (row.value) resize.observe(row.value)
})
onBeforeUnmount(() => resize?.disconnect())

const gridStyle = computed(() => ({
  gridTemplateColumns: `repeat(${kpiRowColumns(cards.value.length, width.value)}, minmax(0, 1fr))`,
}))
</script>

<template>
  <div ref="row" class="flex h-full min-h-0 flex-col gap-3" :aria-busy="updating">
    <!-- No frame draws a title for a bare widget: its own, when it has one. -->
    <div v-if="instance.title || instance.subtitle" class="shrink-0">
      <p v-if="instance.title" class="text-sm font-semibold">{{ instance.title }}</p>
      <p v-if="instance.subtitle" class="text-xs text-[var(--c-text-muted)]">{{ instance.subtitle }}</p>
    </div>

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
        <div class="grid gap-3" :style="gridStyle">
          <div
            v-for="card in cards"
            :key="card.metric"
            class="h-[5.5rem] rounded-[var(--radius-lg)] bg-[var(--c-surface-sunken)]"
          />
        </div>
      </template>

      <div class="grid gap-3 transition-opacity duration-150" :class="updating ? 'opacity-70' : ''" :style="gridStyle">
        <KpiCard
          v-for="card in cards"
          :key="card.metric"
          :label="card.spec.label"
          :value="card.value"
          :identifier="card.spec.identifier"
          :qualifier="card.qualifier"
          :definition="card.definition"
          :percent="card.spec.percent"
          :tone="card.spec.tone"
          :format="config.numberFormat"
          :precision="card.spec.percent ? precision : undefined"
        />
      </div>
    </AsyncBoundary>
  </div>
</template>
