import type { DashboardTemplate } from './types'
import { widget } from './build'

/**
 * For support staff: how fresh the data is, the population in brief, the lookups support is asked
 * for, recent SIM and handset changes, and the devices people actually have. It creates no role and
 * grants nothing - a lookup appears only to someone who already holds it - and it shows no ticket
 * counts or SLAs: there is no source for them.
 */
export const support: DashboardTemplate = {
  key: 'support',
  version: 1,
  name: 'Support',
  description: 'Data freshness, the population in brief, lookups, SIM and handset changes, and popular devices.',
  definition: {
    schemaVersion: 1,
    defaults: { countBy: 'subscribers', range: 'last30' },
    refreshMinutes: 0,
    widgets: [
      widget('w_spfreshness', 'ops.freshness', 'Data freshness', { showPipeline: false, showTac: false }, 0, 0, 12, 4),
      widget('w_spsummary', 'metric.kpiSummary', 'Population', {}, 0, 4, 12, 3),
      widget('w_spnumber', 'tools.quickLookup', 'Look up a number', { target: 'msisdn' }, 0, 7, 4, 3),
      widget('w_spsimcard', 'tools.quickLookup', 'Look up a SIM', { target: 'imsi' }, 4, 7, 4, 3),
      widget('w_spmodel', 'tools.quickLookup', 'Open a device model', { target: 'tac' }, 8, 7, 4, 3),
      widget('w_spchurn', 'change.simHandset', 'SIM and handset changes', {}, 0, 10, 6, 10),
      widget('w_spnewmodels', 'device.newModels', 'New models', {}, 6, 10, 6, 10),
      widget('w_spvendors', 'device.top', 'Top vendors', { dimension: 'vendorCanonical' }, 0, 20, 6, 9),
      widget('w_spmodels', 'device.top', 'Top models', { dimension: 'marketingName', excludeUnknownBucket: true, clickToFilter: false }, 6, 20, 6, 9),
    ],
  },
}
