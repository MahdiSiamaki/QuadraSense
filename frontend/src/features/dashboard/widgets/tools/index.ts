/**
 * Analyst Tools: a shared aggregate Explorer question, a lookup box, and one model's network age.
 * None of them stores an identifier: the Explorer query cannot name one, and what is typed into the
 * lookup box is never saved.
 */
import { defineWidget } from '@/features/dashboard/types/widget'
import type { ExplorerAggregateConfig, ModelNetworkAgeConfig, QuickLookupConfig } from './config'
import ExplorerAggregateWidget from './ExplorerAggregateWidget.vue'
import ExplorerQuerySettings from './ExplorerQuerySettings.vue'
import ModelNetworkAgeWidget from './ModelNetworkAgeWidget.vue'
import QuickLookupWidget from './QuickLookupWidget.vue'

export const definitions = [
  defineWidget<ExplorerAggregateConfig>({
    type: 'tools.explorerAggregate',
    name: 'Explorer question',
    description: 'A saved counts-by-group Explorer question on current bindings or the event log, run on request.',
    tags: ['explorer', 'query', 'count', 'group by', 'aggregate', 'custom', 'ad hoc'],
    aliases: ['custom query', 'query builder', 'saved question', 'count by'],
    icon: 'query',
    unit: 'The question’s own measures: exact counts, or earliest and latest dates.',
    source: 'POST /api/v1/explorer/query (audited, budgeted)',
    timeScope: 'Current bindings, or the event log over the last N days of data or a date condition.',
    caveats: [
      'Runs only when you press Run; each run is audited and counts against the Explorer’s budget.',
      'Counts numbers, SIMs and handsets but never filters, groups or shows them: the definition is shared.',
    ],
    component: ExplorerAggregateWidget,
    settings: ExplorerQuerySettings,
    customFields: ['query'],
    fields: {
      lastDays: {
        label: 'Last days of data',
        section: 'data',
        help: 'For the event log: counted back from its last day each time it runs. Empty when the query has its own date condition.',
      },
      rows: { label: 'Groups to show', section: 'display', visual: true },
      display: { label: 'Show as', section: 'display', options: { table: 'Table', bar: 'Bar chart (one group, one count)' }, visual: true },
    },
    exports: { csv: false, image: false },
  }),

  defineWidget<QuickLookupConfig>({
    type: 'tools.quickLookup',
    name: 'Quick lookup',
    description: 'A box that opens a subscriber, IMSI, relationship or device lookup with the value filled in.',
    tags: ['lookup', 'search', 'msisdn', 'imsi', 'imei', 'tac', 'launcher'],
    aliases: ['search box', 'find subscriber', 'find SIM', 'find device'],
    icon: 'search',
    unit: 'None: a launcher.',
    source: 'Opens the lookup page; the lookup is that page’s own audited request.',
    timeScope: 'None.',
    caveats: ['What is typed is never saved with the dashboard and never put in a URL.', 'Needs the target lookup’s permission.'],
    component: QuickLookupWidget,
    fields: {
      target: {
        label: 'Opens',
        section: 'general',
        options: {
          msisdn: 'Subscriber lookup (number)',
          imsi: 'IMSI search',
          relationships: 'Relationship explorer',
          tac: 'Device (TAC)',
        },
      },
    },
    exports: { csv: false, image: false },
  }),

  defineWidget<ModelNetworkAgeConfig>({
    type: 'tools.modelNetworkAge',
    name: 'Model network age',
    description: 'How long one model (TAC) has been on this network, since the feed first named one of its handsets.',
    tags: ['network age', 'model', 'tac', 'first seen', 'device'],
    aliases: ['model age', 'when did this model arrive', 'first seen'],
    icon: 'device',
    unit: 'Days since first seen.',
    source: 'GET /api/v1/devices/{tac}/network-age',
    timeScope: 'As of the latest daily file.',
    caveats: ['Not the age of any handset.', 'A lower bound for a model the initial dump already listed.'],
    component: ModelNetworkAgeWidget,
    fields: {
      tac: { label: 'TAC', section: 'data', help: 'The 8-digit model code: the first 8 digits of an IMEI.' },
      showDetail: { label: 'Name the model', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),
]
