/**
 * Data Quality: what the feed is made of and where it went wrong - enrichment, the daily checks,
 * the days to be careful with, and the identifier and sequence categories. Facts about the feed,
 * never about a subscriber.
 */
import { defineWidget } from '@/features/dashboard/types/widget'
import type { EnrichmentConfig, FeedTrendConfig, FlaggedDaysConfig, SignalsConfig } from './config'
import EnrichmentWidget from './EnrichmentWidget.vue'
import FeedTrendWidget from './FeedTrendWidget.vue'
import FlaggedDaysWidget from './FlaggedDaysWidget.vue'
import SignalsWidget from './SignalsWidget.vue'

const CHECK_OPTIONS = {
  ShiftedImei: 'IMEIs shifted by one digit',
  MultiNumberSim: 'SIMs with several numbers',
  MalformedImei: 'Malformed IMEIs',
  UnknownDevice: 'Unknown-device rows',
}

export const definitions = [
  defineWidget<EnrichmentConfig>({
    type: 'quality.enrichment',
    name: 'Enrichment breakdown',
    description: 'Every active binding accounted for: enriched from GSMA, unknown device, malformed IMEI, unregistered TAC.',
    tags: ['enrichment', 'gsma', 'tac', 'coverage', 'malformed', 'unknown device', 'quality'],
    aliases: ['TAC coverage breakdown', 'enrichment', 'unregistered TAC'],
    icon: 'quality',
    unit: 'Bindings (number + SIM + handset); the four sum to the active total.',
    source: 'GET /api/v1/dashboard/kpi',
    timeScope: 'Current population, as of the latest refreshed delivery.',
    caveats: ['Only a malformed IMEI is a genuine defect; the unknown-device sentinel is expected.'],
    component: EnrichmentWidget,
    fields: {
      display: { label: 'Show as', section: 'display', options: { figures: 'Four figures', table: 'Table with shares' }, visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<FeedTrendConfig>({
    type: 'quality.feedTrend',
    name: 'Feed quality trend',
    description: 'The daily feed-quality checks over time, as rates against the flagging line or as row counts.',
    tags: ['feed quality', 'trend', 'shifted imei', 'multi-number sim', 'malformed', 'daily', 'checks'],
    aliases: ['feed checks', 'data quality over time', 'file quality'],
    icon: 'trend',
    unit: 'Each check’s rate (0–100%), or rows.',
    source: 'GET /api/v1/quality/days',
    timeScope: 'Every measured day in the dashboard’s range.',
    caveats: ['A day is flagged when a rate is above a multiple of the reference days’ median.', 'Needs import.view.'],
    component: FeedTrendWidget,
    fields: {
      checks: { label: 'Checks', section: 'data', options: CHECK_OPTIONS },
      measure: { label: 'Measure', section: 'data', options: { rate: 'Rate', count: 'Rows' } },
      display: { label: 'Show as', section: 'display', options: { chart: 'Chart', table: 'Table' }, visual: true },
      showReference: {
        label: 'Draw where each check flags a day',
        section: 'display',
        visual: true,
        visibleWhen: (c) => c.measure === 'rate' && c.display === 'chart',
      },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<FlaggedDaysConfig>({
    type: 'quality.flaggedDays',
    name: 'Days to be careful with',
    description: 'Days whose file looked unlike the ordinary days, and days the source never delivered, newest first.',
    tags: ['flagged', 'missing', 'days', 'gaps', 'feed defects', 'calendar'],
    aliases: ['flagged days', 'missing days', 'bad days'],
    icon: 'calendar',
    unit: 'Days.',
    source: 'GET /api/v1/quality/days and /api/v1/imports/freshness',
    timeScope: 'The dashboard’s date range.',
    caveats: ['Needs import.view.', 'Missing days come from the platform’s calendar of expected days.'],
    component: FlaggedDaysWidget,
    fields: {
      include: { label: 'Include', section: 'data', options: { flagged: 'Days out of line', missing: 'Days never delivered' } },
      limit: { label: 'How many', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<SignalsConfig>({
    type: 'quality.signals',
    name: 'Data-quality categories',
    description: 'Identifier faults, sequences the history contradicts, and how long the feed held a binding.',
    tags: ['quality', 'signals', 'identifiers', 'sequences', 'lifetimes', 'categories'],
    aliases: ['data quality', 'identifier faults', 'binding periods'],
    icon: 'quality',
    unit: 'Bindings, numbers, or estimated SIMs or IMEIs.',
    source: 'GET /api/v1/quality/signals',
    timeScope: 'The newest published measures run.',
    caveats: ['A binding can be in more than one category.', 'SIMs and IMEIs are HyperLogLog estimates (about 0.5% error).', 'Needs import.view.'],
    component: SignalsWidget,
    fields: {
      groups: {
        label: 'Groups',
        section: 'data',
        options: { identifiers: 'Identifiers', sequences: 'Sequences', lifetimes: 'Binding periods' },
      },
      unit: { label: 'Count', section: 'data', options: { bindings: 'Bindings', numbers: 'Numbers', sims: 'SIMs (est.)', imeis: 'IMEIs (est.)' } },
      display: { label: 'Show as', section: 'display', options: { table: 'Table', bar: 'Bar chart' }, visual: true },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),
]
