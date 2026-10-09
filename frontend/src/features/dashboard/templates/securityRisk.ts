import type { DashboardTemplate } from './types'
import { widget } from './build'

/**
 * For the people who read risk signals. Readiness and the quality of the evidence come first,
 * because a verdict is only as good as the days behind it; the rule lists run only on an explicit
 * Run, since every run names people and is audited. It grants nothing: every widget still needs
 * risk.view, and a list also the lookup permission of the unit it names.
 */
export const securityRisk: DashboardTemplate = {
  key: 'security-risk',
  version: 1,
  name: 'Security & Risk',
  description: 'Risk-signal readiness, evidence quality, the overview per rule, and the rule lists - run on request.',
  definition: {
    schemaVersion: 1,
    defaults: { range: 'last90' },
    refreshMinutes: 0,
    widgets: [
      widget('w_srstatus', 'risk.status', 'Risk signals: readiness', {}, 0, 0, 12, 7),
      widget('w_srevidence', 'risk.evidenceQuality', 'Evidence quality', {}, 0, 7, 12, 7),
      widget('w_sroverview', 'risk.overview', 'Overview per rule', {}, 0, 14, 12, 8),
      widget('w_srsimchange', 'risk.simChangeEvidence', 'SIM-change evidence', {}, 0, 22, 12, 9),
      widget('w_srdistribution', 'risk.distribution', 'SIMs per handset: distribution', { rule: 'SharedImeiSims30' }, 0, 31, 6, 9),
      widget('w_srdevicetypes', 'risk.deviceTypes', 'Handsets per SIM: device types', { rule: 'HighDeviceCount30' }, 6, 31, 6, 9),
      widget('w_srlistsims', 'risk.list', 'SIMs in many handsets, 30 days', { rule: 'HighDeviceCount30' }, 0, 40, 12, 10),
      widget('w_srlistnumbers', 'risk.list', 'Repeated SIM changes, 7 days', { rule: 'RepeatedSimChange7' }, 0, 50, 12, 10),
    ],
  },
}
