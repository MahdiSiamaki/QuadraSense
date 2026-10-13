/**
 * Overview & Population: the headline figures and the population over time.
 */
import { defineWidget } from '@/features/dashboard/types/widget'
import type { KpiConfig, KpiSummaryConfig, PopulationTrendConfig } from './config'
import { KPI_METRIC_OPTIONS, KPI_METRIC_SPECS, KPI_METRICS, type KpiMetric } from './kpiMetrics'
import KpiWidget from './KpiWidget.vue'
import KpiSummaryWidget from './KpiSummaryWidget.vue'
import PopulationTrendWidget from './PopulationTrendWidget.vue'

const NUMBER_FORMAT = { compact: 'Compact (12.6M)', full: 'Full (12,612,345)' }

const QUALITY_METRICS: KpiMetric[] = ['unknownDeviceBindings', 'malformedImeiBindings', 'unregisteredTacBindings']

export const definitions = [
  defineWidget<KpiConfig>({
    type: 'metric.kpi',
    name: 'Metric KPI',
    description: 'One headline figure - bindings, subscribers, SIM cards, handsets, unknown devices or TAC coverage - with what it does and does not count.',
    tags: ['kpi', 'figure', 'total', 'count', 'headline', 'population'],
    aliases: ['number', 'stat', 'big number', 'active bindings', 'subscribers', 'handsets', 'SIM cards', 'coverage'],
    icon: 'kpi',
    unit: 'The metric’s own unit: bindings, or estimated subscribers, SIM cards or handsets, or a share of bindings.',
    source: 'GET /api/v1/dashboard/kpi',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: [
      'Subscribers, SIM cards and handsets are HyperLogLog estimates (about 0.5% error).',
      'A binding stays active until the source removes it, so dormant numbers and replaced handsets are still counted.',
    ],
    presets: KPI_METRICS.map((metric) => ({
      key: metric,
      name: KPI_METRIC_SPECS[metric].label,
      description: KPI_METRIC_SPECS[metric].definition,
      group: QUALITY_METRICS.includes(metric) ? ('quality' as const) : undefined,
      config: { metric },
      title: KPI_METRIC_SPECS[metric].label,
    })),
    component: KpiWidget,
    fields: {
      metric: { label: 'Figure', section: 'data', options: KPI_METRIC_OPTIONS },
      numberFormat: { label: 'Number format', section: 'display', options: NUMBER_FORMAT, visual: true },
      precision: { label: 'Decimals of a percentage', section: 'display', visual: true, help: 'For TAC coverage and the “% of bindings” qualifiers.' },
      showDefinition: { label: 'Show the definition (the “i”)', section: 'display', visual: true },
      showQualifier: { label: 'Show the qualifier under the figure', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
    chrome: 'bare',
    height: 'content',
  }),

  defineWidget<KpiSummaryConfig>({
    type: 'metric.kpiSummary',
    name: 'KPI summary',
    description: 'Several headline figures in a row, from one request - the dashboard’s classic KPI row.',
    tags: ['kpi', 'summary', 'figures', 'headline', 'population'],
    aliases: ['kpi row', 'overview', 'totals'],
    icon: 'kpi',
    unit: 'Each figure’s own unit, named on its card.',
    source: 'GET /api/v1/dashboard/kpi',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: ['Subscribers, SIM cards and handsets are HyperLogLog estimates (about 0.5% error).'],
    component: KpiSummaryWidget,
    fields: {
      metrics: { label: 'Figures', section: 'data', options: KPI_METRIC_OPTIONS },
      numberFormat: { label: 'Number format', section: 'display', options: NUMBER_FORMAT, visual: true },
      precision: { label: 'Decimals of a percentage', section: 'display', visual: true },
      showDefinition: { label: 'Show definitions (the “i”)', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<PopulationTrendConfig>({
    type: 'population.trend',
    name: 'Active population trend',
    description: 'Active bindings over time, by day, week or month, from the running total of the daily files.',
    tags: ['trend', 'population', 'growth', 'over time', 'history'],
    aliases: ['bindings over time', 'active bindings trend', 'population history'],
    icon: 'trend',
    unit: 'Bindings (number + SIM + handset), never subscribers.',
    source: 'GET /api/v1/dashboard/daily-changes (the running total)',
    timeScope: 'Every delivered day in the dashboard’s range; a missing day is a gap.',
    caveats: [
      'Network-wide: the daily marts have no vendor or device breakdown.',
      'A week or month shows its last delivered day, never a sum of days.',
    ],
    component: PopulationTrendWidget,
    fields: {
      grain: { label: 'Grain', section: 'data', options: { day: 'Day', week: 'ISO week', month: 'Month' } },
      chart: { label: 'Chart', section: 'display', options: { line: 'Line', area: 'Area' }, visual: true },
      showFlagged: { label: 'Shade days the feed looked wrong', section: 'display', visual: true, help: 'Needs import.view; without it the chart is drawn unshaded.' },
      showMissing: { label: 'Name missing days', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),
]
