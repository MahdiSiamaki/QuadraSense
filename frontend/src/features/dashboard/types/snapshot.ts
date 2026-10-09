/**
 * What `POST /api/v1/dashboards/export/snapshot` returns as `data` for each widget type - the one
 * agreement between the server's loaders (`Sqm.Application.Dashboards.Data`) and the widgets.
 *
 * The rule: **data is always an object keyed by source**, each value exactly what that source's own
 * endpoint returns (camelCase), so a widget draws an export with the same code as a live query -
 * `useWidgetData(props, query, (raw) => (raw as SnapshotData<'metric.kpi'>).kpi)`.
 *
 * Optional sources are null when the person may not read them: a dashboard card shades flagged days
 * only for someone with import.view, and an export does the same.
 *
 * Types that never carry data: `risk.list` and `tools.explorerAggregate` are `interactive` (re-run
 * through their own audited endpoints), `tools.quickLookup` is `noData` (a launcher).
 */
import type { CapabilitySupport, DailyChange, DailyChurn, DimensionCount, KpiSummary, VendorMovementResponse } from '@/api/dashboard'
import type { DeviceDetail } from '@/api/devices'
import type { ImportPage, QueueBlockage, SourceFreshness, WorkerHealth } from '@/api/imports'
import type { ModelArrivals, ModelArrivalStatus, ModelNetworkAge, NewModelsResponse } from '@/api/modelArrivals'
import type { FeedQualityResponse, QualitySignalsResponse } from '@/api/quality'
import type { RiskChangeDay, RiskDeviceTypes, RiskDistribution, RiskOverview, RiskStatus } from '@/api/risk'
import type { TacVersion } from '@/features/imports/useTacVersions'

export interface SnapshotShapes {
  'metric.kpi': { kpi: KpiSummary }
  'metric.kpiSummary': { kpi: KpiSummary }
  /** feedQuality and freshness need import.view: they shade flagged days and name missing ones. */
  'population.trend': { changes: DailyChange[]; feedQuality: FeedQualityResponse | null; freshness: SourceFreshness[] | null }
  'device.top': { rows: DimensionCount[] }
  'device.distribution': { rows: DimensionCount[] }
  'device.classMix': { rows: DimensionCount[] }
  /** tacVersions (import.view) gives the GSMA row count the VoLTE note quotes. */
  'device.capabilities': { capabilities: CapabilitySupport[]; tacVersions: TacVersion[] | null }
  /** models is null while the arrivals table is not ready; status says why. */
  'device.newModels': { status: ModelArrivalStatus; models: NewModelsResponse | null }
  'device.modelArrivals': { status: ModelArrivalStatus; arrivals: ModelArrivals | null }
  'change.daily': { changes: DailyChange[]; feedQuality: FeedQualityResponse | null; freshness: SourceFreshness[] | null }
  'change.simHandset': { churn: DailyChurn[]; feedQuality: FeedQualityResponse | null }
  'change.vendorMovement': { movement: VendorMovementResponse }
  /** Only the series the chosen metric needs is set; the other is null. */
  'change.periodComparison': { changes: DailyChange[] | null; churn: DailyChurn[] | null }
  'risk.status': { status: RiskStatus }
  /** The second source is null when risk signals are not ready; status.notReady says why. */
  'risk.overview': { status: RiskStatus; overview: RiskOverview | null }
  'risk.distribution': { status: RiskStatus; distribution: RiskDistribution | null }
  'risk.deviceTypes': { status: RiskStatus; deviceTypes: RiskDeviceTypes | null }
  'risk.simChangeEvidence': { status: RiskStatus; daily: { days: RiskChangeDay[] } | null }
  'risk.evidenceQuality': { status: RiskStatus; overview: RiskOverview | null }
  'quality.enrichment': { kpi: KpiSummary }
  'quality.feedTrend': { feedQuality: FeedQualityResponse }
  'quality.flaggedDays': { feedQuality: FeedQualityResponse; freshness: SourceFreshness[] }
  'quality.signals': { signals: QualitySignalsResponse }
  /** kpi is the unfiltered summary: its deliveryDate tells "served" from "imported". */
  'ops.freshness': { freshness: SourceFreshness[]; workerHealth: WorkerHealth; tacVersions: TacVersion[]; kpi: KpiSummary | null }
  'ops.recentImports': { page: ImportPage }
  'ops.importStatus': { workerHealth: WorkerHealth; freshness: SourceFreshness[] }
  'ops.workerHealth': { workerHealth: WorkerHealth; freshness: SourceFreshness[] }
  'ops.blockages': { blockages: QueueBlockage[]; freshness: SourceFreshness[] }
  'ops.tacVersion': { tacVersions: TacVersion[] }
  /** device is the model's detail when showDetail is on; null otherwise or when unknown. */
  'tools.modelNetworkAge': { age: ModelNetworkAge | null; device: DeviceDetail | null }
}

export type SnapshotData<T extends keyof SnapshotShapes> = SnapshotShapes[T]
