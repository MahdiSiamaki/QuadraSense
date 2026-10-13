/**
 * Risk & Investigation: the risk signals' state, counts, evidence and - on request - who they list.
 * Everything but the list is counts and names nobody; the list runs only on Run and is audited.
 */
import { RULE_TITLES } from '@/api/risk'
import { defineWidget } from '@/features/dashboard/types/widget'
import type {
  EvidenceQualityConfig,
  RiskDeviceTypesConfig,
  RiskDistributionConfig,
  RiskListConfig,
  RiskOverviewConfig,
  RiskStatusConfig,
  SimChangeEvidenceConfig,
} from './config'
import EvidenceQualityWidget from './EvidenceQualityWidget.vue'
import RiskDeviceTypesWidget from './RiskDeviceTypesWidget.vue'
import RiskDistributionWidget from './RiskDistributionWidget.vue'
import RiskListWidget from './RiskListWidget.vue'
import RiskOverviewWidget from './RiskOverviewWidget.vue'
import RiskStatusWidget from './RiskStatusWidget.vue'
import SimChangeEvidenceWidget from './SimChangeEvidenceWidget.vue'
import { RULES } from './shared'

const RULE_OPTIONS = Object.fromEntries(RULES.map((r) => [r, RULE_TITLES[r] ?? r]))
const DEVICE_RULE_OPTIONS = Object.fromEntries(RULES.filter((r) => r !== 'RepeatedSimChange7').map((r) => [r, RULE_TITLES[r] ?? r]))

const AS_OF = 'The published risk run: each rule’s own window, ending at its as-of day - not the dashboard’s date range.'
const OBSERVED = 'Observed, not proven: a level states how strong the evidence is, never that anyone did anything.'
const NAMES_NOBODY = 'Counts only: names nobody.'

export const definitions = [
  defineWidget<RiskStatusConfig>({
    type: 'risk.status',
    name: 'Risk status',
    description: 'Whether the risk figures can be read: data through, the published run, staleness and the rule set.',
    tags: ['risk', 'status', 'rules', 'thresholds', 'stale', 'as of'],
    aliases: ['risk rules', 'rule set', 'risk freshness'],
    icon: 'shield',
    unit: 'States and rule settings; no counts.',
    source: 'GET /api/v1/risk/status',
    timeScope: AS_OF,
    caveats: [OBSERVED],
    component: RiskStatusWidget,
    fields: {
      showRules: { label: 'List the rules with their windows and thresholds', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<RiskOverviewConfig>({
    type: 'risk.overview',
    name: 'Risk overview',
    description: 'How many entities each rule lists at its threshold, at what level, and how many only through feed defects.',
    tags: ['risk', 'overview', 'counts', 'signals', 'listed', 'levels'],
    aliases: ['risk signals', 'risk summary', 'how many flagged'],
    icon: 'shield',
    unit: 'Entities listed - IMEIs, SIMs or numbers, per the rule.',
    source: 'GET /api/v1/risk/overview',
    timeScope: AS_OF,
    caveats: [NAMES_NOBODY, OBSERVED],
    component: RiskOverviewWidget,
    fields: {
      rules: { label: 'Rules', section: 'data', options: RULE_OPTIONS },
      display: { label: 'Show as', section: 'display', options: { table: 'Table with levels', bar: 'Bar chart' }, visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<RiskDistributionConfig>({
    type: 'risk.distribution',
    name: 'Threshold distribution',
    description: 'Where a rule’s threshold sits: every stored entity by its value, with the listed range shaded.',
    tags: ['risk', 'distribution', 'threshold', 'histogram', 'calibration'],
    aliases: ['where the threshold sits', 'risk histogram'],
    icon: 'bar',
    unit: 'Entities (log scale) by the rule’s own measure.',
    source: 'GET /api/v1/risk/distribution',
    timeScope: AS_OF,
    caveats: [NAMES_NOBODY, 'Values under the rule’s storage floor are not stored and do not appear.'],
    component: RiskDistributionWidget,
    fields: {
      rule: { label: 'Rule', section: 'data', options: RULE_OPTIONS },
      values: {
        label: 'Counts',
        section: 'display',
        options: { both: 'Clean and before the screens', clean: 'Clean only', raw: 'Before the screens only' },
        visual: true,
      },
      showThreshold: { label: 'Shade the listed range', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<RiskDeviceTypesConfig>({
    type: 'risk.deviceTypes',
    name: 'Risk by device type',
    description: 'What a rule’s list is made of, by GSMA device type.',
    tags: ['risk', 'device type', 'breakdown', 'iot', 'modem'],
    aliases: ['flagged device types', 'what is listed'],
    icon: 'table',
    unit: 'IMEIs or SIMs listed at the configured threshold.',
    source: 'GET /api/v1/risk/device-types',
    timeScope: AS_OF,
    caveats: [NAMES_NOBODY, 'For SIM rules the type is that of the SIM’s most frequent TAC in 20 days.'],
    component: RiskDeviceTypesWidget,
    fields: {
      rule: { label: 'Rule', section: 'data', options: DEVICE_RULE_OPTIONS },
      limit: { label: 'How many types', section: 'data' },
      display: { label: 'Show as', section: 'display', options: { table: 'Table', bar: 'Bar chart' }, visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<SimChangeEvidenceConfig>({
    type: 'risk.simChangeEvidence',
    name: 'SIM-change evidence',
    description: 'Numbers whose SIM changed per day: what the risk lists count, and what the screens set aside.',
    tags: ['risk', 'sim change', 'evidence', 'set aside', 'feed defects', 'daily'],
    aliases: ['SIM swap evidence', 'counted vs set aside'],
    icon: 'trend',
    unit: 'Numbers (MSISDNs) whose SIM changed, per day.',
    source: 'GET /api/v1/risk/daily',
    timeScope: 'Every day in the dashboard’s range.',
    caveats: [NAMES_NOBODY, 'Set aside: the feed listed one of the number’s SIMs under several numbers that day.'],
    component: SimChangeEvidenceWidget,
    fields: {
      series: { label: 'Series', section: 'data', options: { changes: 'SIM changes', setAside: 'Set aside as feed defects' } },
      showFlagged: { label: 'Shade days the feed-quality monitor flagged', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<EvidenceQualityConfig>({
    type: 'risk.evidenceQuality',
    name: 'Evidence quality',
    description: 'Per rule, how many are over the threshold on clean evidence and how many only through feed defects.',
    tags: ['risk', 'evidence', 'data quality', 'feed defects', 'capped', 'clean'],
    aliases: ['clean evidence', 'set aside share', 'held at anomaly'],
    icon: 'quality',
    unit: 'Entities - IMEIs, SIMs or numbers, per the rule.',
    source: 'GET /api/v1/risk/overview with the rules from /risk/status',
    timeScope: AS_OF,
    caveats: [NAMES_NOBODY, 'An all-time rule has no raw count, so nothing is set aside for it.'],
    component: EvidenceQualityWidget,
    fields: {
      rules: { label: 'Rules', section: 'data', options: RULE_OPTIONS },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<RiskListConfig>({
    type: 'risk.list',
    name: 'Risk list',
    description: 'The IMEIs, SIMs or numbers a rule lists - run on request, audited, and opened in place.',
    tags: ['risk', 'list', 'investigate', 'identifiers', 'who', 'entities'],
    aliases: ['flagged SIMs', 'shared IMEIs', 'SIM changes list', 'suspicious list'],
    icon: 'list',
    unit: 'Entities - IMEIs, SIMs or numbers, per the rule.',
    source: 'POST /api/v1/risk/list (audited)',
    timeScope: AS_OF,
    caveats: [
      'Lists identifiers: runs only when you press Run, and each run is audited.',
      'Needs the rule’s lookup permission as well as risk.view.',
      'A threshold override is for this widget only; the configured threshold never changes from here.',
      OBSERVED,
    ],
    presets: [
      { key: 'sims', name: 'SIMs on many IMEIs', description: 'SIMs added to more handsets in 30 days than the threshold.', config: { rule: 'HighDeviceCount30' }, title: 'SIMs on many IMEIs' },
      { key: 'imeis', name: 'Shared IMEIs', description: 'IMEIs more SIMs were added to in 30 days than the threshold.', config: { rule: 'SharedImeiSims30' }, title: 'Shared IMEIs' },
      { key: 'numbers', name: 'Repeated SIM changes', description: 'Numbers with a SIM change on more days in 7 than the threshold.', config: { rule: 'RepeatedSimChange7' }, title: 'Repeated SIM changes' },
    ],
    component: RiskListWidget,
    fields: {
      rule: { label: 'Rule', section: 'data', options: RULE_OPTIONS },
      view: {
        label: 'View',
        section: 'data',
        options: { risk: 'Risk', dataQuality: 'Data quality (not risk)' },
        help: 'Data quality lists what crossed only through feed defects. Not offered by all-time rules.',
      },
      deviceTypes: {
        label: 'Device types',
        section: 'data',
        help: 'GSMA device types, exactly as named and separated by commas (e.g. Smartphone, Tablet). Empty for all. Not used by the per-number rule.',
        visibleWhen: (c) => c.rule !== 'RepeatedSimChange7',
      },
      thresholdOverride: {
        label: 'Threshold override (more than)',
        section: 'advanced',
        help: 'Empty uses the configured threshold. Must not ask for values below the rule’s storage floor.',
      },
      tacsThresholdOverride: {
        label: 'TAC threshold override (more than)',
        section: 'advanced',
        visibleWhen: (c) => c.rule === 'Randomisation20',
      },
      pageSize: { label: 'Rows per page', section: 'display', visual: true },
    },
    exports: { csv: false, image: false },
  }),
]
