/**
 * Change & Movement: what the daily files added and removed, who changed SIM or handset, which
 * vendors moved, and how a period compares with another.
 */
import { defineWidget, type FieldError } from '@/features/dashboard/types/widget'
import type { ChangeDailyConfig, PeriodComparisonConfig, SimHandsetConfig, VendorMovementConfig } from './config'
import ChangeDailyWidget from './ChangeDailyWidget.vue'
import PeriodComparisonWidget from './PeriodComparisonWidget.vue'
import SimHandsetWidget from './SimHandsetWidget.vue'
import VendorMovementWidget from './VendorMovementWidget.vue'

const NETWORK_WIDE = 'Network-wide: the daily marts have no vendor or device breakdown, so the dashboard’s filters do not apply.'
const SHADING_NEEDS_IMPORT_VIEW = 'Needs import.view; without it the chart is drawn unshaded.'

export const definitions = [
  defineWidget<ChangeDailyConfig>({
    type: 'change.daily',
    name: 'Daily change',
    description: 'Each day’s adds and removes, and the running total of active bindings.',
    tags: ['daily', 'change', 'adds', 'removes', 'net', 'flow', 'over time'],
    aliases: ['daily adds', 'daily removes', 'net change', 'activations', 'deactivations'],
    icon: 'trend',
    unit: 'Bindings (number + SIM + handset) - add and remove events, and the running total.',
    source: 'GET /api/v1/dashboard/daily-changes',
    timeScope: 'Every delivered day in the dashboard’s range; a missing day is a gap.',
    caveats: [NETWORK_WIDE, 'Days whose file looked wrong (shifted IMEIs, SIMs with several numbers) are shaded when you may see them.'],
    component: ChangeDailyWidget,
    fields: {
      series: {
        label: 'Series',
        section: 'data',
        options: { added: 'Added', removed: 'Removed', net: 'Net (added minus removed)', cumulative: 'Running total' },
      },
      chart: { label: 'The day’s flow as', section: 'display', options: { bar: 'Bars', line: 'Lines' }, visual: true },
      showFlagged: { label: 'Shade days the feed looked wrong', section: 'display', visual: true, help: SHADING_NEEDS_IMPORT_VIEW },
      showMissing: { label: 'Name missing days', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<SimHandsetConfig>({
    type: 'change.simHandset',
    name: 'SIM and handset changes',
    description: 'Numbers that moved to a different SIM or a different handset, per day.',
    tags: ['sim change', 'sim swap', 'handset change', 'device change', 'churn', 'daily'],
    aliases: ['SIM swap', 'device swap', 'handset swap', 'churn'],
    icon: 'movement',
    unit: 'Numbers (MSISDNs) per day, by the same-day definition.',
    source: 'GET /api/v1/dashboard/daily-churn',
    timeScope: 'Every delivered day in the dashboard’s range.',
    caveats: [
      NETWORK_WIDE,
      'Same-day definition: a change spanning midnight is not counted, so these are a floor, not a total.',
      'From 2026-07-27 SIMs carried several numbers a day and from 2026-09-15 IMEIs arrived shifted - both show as changes that did not happen.',
    ],
    component: SimHandsetWidget,
    fields: {
      series: { label: 'Series', section: 'data', options: { simChanges: 'SIM changes', deviceChanges: 'Handset changes' } },
      chart: { label: 'Draw as', section: 'display', options: { line: 'Lines', bar: 'Bars' }, visual: true },
      showFlagged: { label: 'Shade days the feed looked wrong', section: 'display', visual: true, help: SHADING_NEEDS_IMPORT_VIEW },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<VendorMovementConfig>({
    type: 'change.vendorMovement',
    name: 'Vendor movement',
    description: 'Vendors by add/remove movement, by share of the population, or by growth against the network.',
    tags: ['vendor', 'movement', 'share', 'growth', 'gainers', 'losers', 'market share'],
    aliases: ['vendor growth', 'vendor share', 'brand movement'],
    icon: 'movement',
    unit: 'Movement: add and remove events. Share and growth: active bindings.',
    source: 'GET /api/v1/dashboard/vendor-movement',
    timeScope: 'Movement: the dashboard’s range. Share: the latest delivery. Growth: the first delivery against the latest.',
    caveats: [
      'Movement counts events, not devices - a SIM moving between handsets thirty times is thirty adds and thirty removes.',
      'Growth is shown against the network, which fell over the same span; when it starts at the initial dump, part of that fall is the dump’s over-count resolving.',
    ],
    component: VendorMovementWidget,
    fields: {
      rank: {
        label: 'Measure',
        section: 'data',
        options: { movement: 'Movement (events in the range)', share: 'Share (population now)', growth: 'Growth (against the network)' },
      },
      limit: { label: 'How many vendors', section: 'data' },
      normalised: {
        label: 'Net movement as a % of the vendor’s population',
        section: 'data',
        visibleWhen: (c) => c.rank === 'movement',
        help: 'Makes a vendor with fifty million bindings comparable to one with fifty thousand.',
      },
      display: { label: 'Show as', section: 'display', options: { chart: 'Chart', table: 'Table' }, visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<PeriodComparisonConfig>({
    type: 'change.periodComparison',
    name: 'Period comparison',
    description: 'One daily figure for the dashboard’s range against the period before it, or against dates you choose.',
    tags: ['compare', 'comparison', 'period', 'previous', 'baseline', 'versus', 'week over week', 'month over month'],
    aliases: ['vs previous period', 'period over period', 'delta'],
    icon: 'compare',
    unit: 'The metric’s own: add or remove events, numbers changing SIM or handset, or active bindings.',
    source: 'GET /api/v1/dashboard/daily-changes or /daily-churn',
    timeScope: 'The dashboard’s range against a baseline of the same length before it, or fixed dates.',
    caveats: [
      NETWORK_WIDE,
      'A missing day adds nothing; when the two periods have different numbers of delivered days, compare daily averages.',
    ],
    component: PeriodComparisonWidget,
    fields: {
      metric: {
        label: 'Figure',
        section: 'data',
        options: {
          net: 'Net change',
          added: 'Bindings added',
          removed: 'Bindings removed',
          population: 'Active bindings',
          simChanges: 'SIM changes',
          deviceChanges: 'Handset changes',
        },
      },
      aggregate: {
        label: 'Over each period',
        section: 'data',
        options: { total: 'Total (population: the last day)', dailyAverage: 'Average per delivered day' },
      },
      baseline: { label: 'Compare with', section: 'data', options: { previous: 'The period before', custom: 'Fixed dates' } },
      baselineFrom: { label: 'Baseline from', section: 'data', visibleWhen: (c) => c.baseline === 'custom' },
      baselineTo: { label: 'Baseline to', section: 'data', visibleWhen: (c) => c.baseline === 'custom' },
      change: {
        label: 'Show the change as',
        section: 'display',
        options: { both: 'Both', absolute: 'A difference', percent: 'A percentage' },
        visual: true,
      },
    },
    validate: (c) => {
      const errors: FieldError[] = []
      if (c.baseline !== 'custom') return errors
      if (!c.baselineFrom) errors.push({ field: 'baselineFrom', message: 'A fixed baseline needs a start date.' })
      if (!c.baselineTo) errors.push({ field: 'baselineTo', message: 'A fixed baseline needs an end date.' })
      if (c.baselineFrom && c.baselineTo && c.baselineFrom > c.baselineTo) {
        errors.push({ field: 'baselineTo', message: 'The baseline ends before it starts.' })
      }
      return errors
    },
    exports: { csv: true, image: true },
  }),
]
