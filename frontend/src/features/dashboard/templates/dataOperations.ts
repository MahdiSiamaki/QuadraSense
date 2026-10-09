import type { DashboardTemplate } from './types'
import { widget } from './build'

/**
 * For the people who keep the data flowing: freshness and the queue first, then what is blocking,
 * what arrived, and how the files themselves looked. Everything here needs import.view; none of it
 * names a subscriber.
 */
export const dataOperations: DashboardTemplate = {
  key: 'data-operations',
  version: 1,
  name: 'Data Operations & Quality',
  description: 'Freshness, the import queue and workers, blocked and missing days, recent imports and feed quality.',
  definition: {
    schemaVersion: 1,
    defaults: { range: 'last90' },
    refreshMinutes: 5,
    widgets: [
      widget('w_dofreshness', 'ops.freshness', 'Data freshness', {}, 0, 0, 12, 4),
      widget('w_doworkers', 'ops.workerHealth', 'Workers and rebuilds', {}, 0, 4, 6, 4),
      widget('w_doqueue', 'ops.importStatus', 'Import queue', {}, 6, 4, 6, 4),
      widget('w_doblockages', 'ops.blockages', 'Blocked days', {}, 0, 8, 6, 6),
      widget('w_dotacversion', 'ops.tacVersion', 'GSMA version', {}, 6, 8, 6, 6),
      widget('w_dorecent', 'ops.recentImports', 'Recent imports', {}, 0, 14, 12, 8),
      widget('w_dofeedtrend', 'quality.feedTrend', 'Feed quality', {}, 0, 22, 12, 9),
      widget('w_doflagged', 'quality.flaggedDays', 'Flagged and missing days', {}, 0, 31, 6, 9),
      widget('w_dosignals', 'quality.signals', 'Data-quality signals', {}, 6, 31, 6, 9),
      widget('w_doenrichment', 'quality.enrichment', 'Enrichment breakdown', {}, 0, 40, 12, 5),
    ],
  },
}
