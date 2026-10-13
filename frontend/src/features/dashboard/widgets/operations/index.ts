/**
 * Data Operations: how current the data is and how the pipeline that brings it is doing. All behind
 * import.view - the operator's view, never a subscriber's.
 */
import { statusLabel, type ImportStatus } from '@/api/imports'
import { defineWidget } from '@/features/dashboard/types/widget'
import { CONTRACT } from '@/features/dashboard/registry/contract'
import type {
  BlockagesConfig,
  FreshnessConfig,
  ImportStatusConfig,
  RecentImportsConfig,
  TacVersionConfig,
  WorkerHealthConfig,
} from './config'
import BlockagesWidget from './BlockagesWidget.vue'
import FreshnessWidget from './FreshnessWidget.vue'
import ImportStatusWidget from './ImportStatusWidget.vue'
import RecentImportsWidget from './RecentImportsWidget.vue'
import TacVersionWidget from './TacVersionWidget.vue'
import WorkerHealthWidget from './WorkerHealthWidget.vue'

const IMPORT_VIEW = 'Needs import.view.'
const LIVE = 'Live: the state now, not a date range.'

const STATUS_OPTIONS = Object.fromEntries(
  ((CONTRACT.types['ops.recentImports']?.config['statuses']?.values ?? []) as ImportStatus[]).map((s) => [s, statusLabel(s)]),
)

export const definitions = [
  defineWidget<FreshnessConfig>({
    type: 'ops.freshness',
    name: 'Data freshness',
    description: 'How current the data is: the latest day, the days never delivered, the device database and the pipeline.',
    tags: ['freshness', 'data through', 'missing days', 'lag', 'tac version', 'pipeline', 'imports'],
    aliases: ['data through', 'how current', 'freshness', 'last import'],
    icon: 'clock',
    unit: 'Days and states.',
    source: 'GET /api/v1/imports/freshness, /imports/worker-health, /tac-versions and /dashboard/kpi',
    timeScope: LIVE,
    caveats: [
      IMPORT_VIEW,
      'Days behind: green to 35, amber to 45, red beyond - from the observed four-week delivery lag.',
      'During a mart rebuild the figures are from the last complete delivery, and the card says so.',
    ],
    component: FreshnessWidget,
    fields: {
      showTac: { label: 'Show the device database', section: 'display', visual: true },
      showPipeline: { label: 'Show the import pipeline', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
    height: 'content',
  }),

  defineWidget<RecentImportsConfig>({
    type: 'ops.recentImports',
    name: 'Recent imports',
    description: 'The newest import jobs, of one source or in chosen states.',
    tags: ['imports', 'jobs', 'history', 'files', 'failed', 'recent'],
    aliases: ['import history', 'latest files', 'failed imports'],
    icon: 'upload',
    unit: 'Import jobs.',
    source: 'GET /api/v1/imports',
    timeScope: LIVE,
    caveats: [IMPORT_VIEW, 'A superseded import is dimmed, not hidden.'],
    presets: [
      { key: 'failed', name: 'Failed imports', description: 'Jobs that failed or were quarantined.', config: { statuses: ['Failed', 'Quarantined'] }, title: 'Failed imports' },
    ],
    component: RecentImportsWidget,
    fields: {
      source: { label: 'Source', section: 'data', options: { any: 'Any', SQM: 'Daily files (SQM)', TAC: 'Device database (TAC)' } },
      statuses: { label: 'States', section: 'data', options: STATUS_OPTIONS, help: 'None chosen shows every state.' },
      limit: { label: 'How many', section: 'data' },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<ImportStatusConfig>({
    type: 'ops.importStatus',
    name: 'Import status',
    description: 'The pipeline in one line: running, queued or idle, failures in 24 hours, the last file landed.',
    tags: ['imports', 'status', 'queue', 'running', 'pipeline'],
    aliases: ['pipeline status', 'queue'],
    icon: 'upload',
    unit: 'Import jobs.',
    source: 'GET /api/v1/imports/worker-health and /imports/freshness',
    timeScope: LIVE,
    caveats: [IMPORT_VIEW],
    component: ImportStatusWidget,
    fields: {},
    exports: { csv: true, image: true },
  }),

  defineWidget<WorkerHealthConfig>({
    type: 'ops.workerHealth',
    name: 'Worker health',
    description: 'The import workers: alive, holding jobs, stale leases, and whether figures wait for queued files.',
    tags: ['worker', 'health', 'leases', 'stalled', 'pipeline'],
    aliases: ['import worker', 'workers'],
    icon: 'server',
    unit: 'Workers and jobs.',
    source: 'GET /api/v1/imports/worker-health and /imports/freshness',
    timeScope: LIVE,
    caveats: [IMPORT_VIEW],
    component: WorkerHealthWidget,
    fields: {
      showPendingRebuild: { label: 'Say when figures wait for queued files', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<BlockagesConfig>({
    type: 'ops.blockages',
    name: 'Blocked days',
    description: 'Days that did not land and hold later days of their source back.',
    tags: ['blocked', 'blockages', 'failed', 'queue', 'held back'],
    aliases: ['stuck imports', 'blocked queue'],
    icon: 'list',
    unit: 'Days.',
    source: 'GET /api/v1/imports/blockages',
    timeScope: LIVE,
    caveats: [IMPORT_VIEW],
    component: BlockagesWidget,
    fields: {
      limit: { label: 'How many', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),

  defineWidget<TacVersionConfig>({
    type: 'ops.tacVersion',
    name: 'Device database version',
    description: 'Which GSMA device database is active, and whether a newer one waits for activation.',
    tags: ['tac', 'gsma', 'version', 'device database', 'activation'],
    aliases: ['TAC version', 'GSMA version'],
    icon: 'tag',
    unit: 'Versions.',
    source: 'GET /api/v1/tac-versions',
    timeScope: LIVE,
    caveats: [IMPORT_VIEW, 'Activating a version changes the manufacturer and model behind every figure.'],
    component: TacVersionWidget,
    fields: {
      showPending: { label: 'Show versions waiting for activation', section: 'display', visual: true },
    },
    exports: { csv: true, image: true },
  }),
]
