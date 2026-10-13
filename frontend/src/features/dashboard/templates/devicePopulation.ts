import type { DashboardTemplate } from './types'

/**
 * The page `/` used to be, as a template: the same cards in the same order, with the same titles and
 * meaning. Every caption and caveat the page carried lives in the widgets now; nothing was dropped.
 *
 * It is also what `/` opens for anyone who has not chosen a default, so an old link with
 * ?vendor=…&countBy=handsets lands on the view it always did.
 */
export const devicePopulation: DashboardTemplate = {
  key: 'device-population',
  version: 3,
  name: 'Device population',
  description: 'Active device–SIM bindings across the network, enriched with GSMA device data.',
  definition: {
    schemaVersion: 1,
    defaults: { countBy: 'bindings', includeUnknownDevice: true, range: 'all' },
    refreshMinutes: 0,
    widgets: [
      { id: 'w_dpfreshness', type: 'ops.freshness', configVersion: 1, title: 'Data freshness', subtitle: null,
        config: { showPipeline: true, showTac: true }, overrides: {}, layout: { x: 0, y: 0, w: 12, h: 4 } },

      // The page's KPI row: one card, one menu, the six figures side by side as they always were.
      { id: 'w_dpkpirow', type: 'metric.kpiSummary', configVersion: 1, title: 'Key figures', subtitle: null,
        config: {
          metrics: ['activeBindings', 'distinctSubscribers', 'distinctSims', 'distinctDevices', 'unknownDeviceBindings', 'tacCoveragePercent'],
          numberFormat: 'compact', precision: 1, showDefinition: true,
        },
        overrides: {}, layout: { x: 0, y: 4, w: 12, h: 3 } },

      { id: 'w_dpdailychange', type: 'change.daily', configVersion: 1, title: 'Daily change', subtitle: null,
        config: { series: ['added', 'removed', 'cumulative'], chart: 'bar', showFlagged: true, showMissing: true },
        overrides: {}, layout: { x: 0, y: 7, w: 12, h: 10 } },

      { id: 'w_dpchurn', type: 'change.simHandset', configVersion: 1, title: 'SIM and handset changes', subtitle: null,
        config: { series: ['simChanges', 'deviceChanges'], chart: 'line', showFlagged: true },
        overrides: {}, layout: { x: 0, y: 17, w: 6, h: 10 } },
      { id: 'w_dpvendormove', type: 'change.vendorMovement', configVersion: 1, title: 'Vendor movement', subtitle: null,
        config: { rank: 'movement', limit: 5, display: 'chart', normalised: false },
        overrides: {}, layout: { x: 6, y: 17, w: 6, h: 10 } },

      { id: 'w_dpclassmix', type: 'device.classMix', configVersion: 1, title: 'Device class mix', subtitle: null,
        config: { display: 'composition' }, overrides: {}, layout: { x: 0, y: 27, w: 12, h: 3 } },

      { id: 'w_dpcapability', type: 'device.capabilities', configVersion: 1, title: 'Network & SIM capability', subtitle: null,
        config: { capabilities: ['LTE', '5G', 'eSIM', 'IMS emergency calling'], showVolteNote: true },
        overrides: {}, layout: { x: 0, y: 30, w: 12, h: 7 } },

      { id: 'w_dpvendors', type: 'device.top', configVersion: 1, title: 'Top vendors', subtitle: null,
        config: { dimension: 'vendorCanonical', limit: 10, display: 'bar', sort: 'count', showValues: true, showPercent: true, excludeUnknownBucket: false, clickToFilter: true },
        overrides: {}, layout: { x: 0, y: 37, w: 6, h: 9 } },
      { id: 'w_dpmodels', type: 'device.top', configVersion: 1, title: 'Top models', subtitle: null,
        config: { dimension: 'marketingName', limit: 10, display: 'bar', sort: 'count', showValues: true, showPercent: true, excludeUnknownBucket: true, clickToFilter: false },
        overrides: {}, layout: { x: 6, y: 37, w: 6, h: 9 } },

      { id: 'w_dpdevicetypes', type: 'device.distribution', configVersion: 1, title: 'Device types', subtitle: null,
        config: { dimension: 'deviceType', limit: 10, display: 'table', showPercent: true, clickToFilter: true },
        overrides: {}, layout: { x: 0, y: 46, w: 6, h: 9 } },
      { id: 'w_dposes', type: 'device.distribution', configVersion: 1, title: 'Operating systems', subtitle: null,
        config: { dimension: 'operatingSystem', limit: 10, display: 'table', showPercent: true, clickToFilter: true },
        overrides: {}, layout: { x: 6, y: 46, w: 6, h: 9 } },

      { id: 'w_dpenrichment', type: 'quality.enrichment', configVersion: 1, title: 'Enrichment breakdown', subtitle: null,
        config: { display: 'figures' }, overrides: {}, layout: { x: 0, y: 55, w: 12, h: 5 } },
    ],
  },
}
