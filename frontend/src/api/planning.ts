import { keepPreviousData, useMutation, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/*
  Network and product planning. Mirrors Sqm.Contracts.Planning.

  Every class is decided by the server, from the planning snapshot the import worker builds per
  delivery (docs/architecture/19-network-planning.md); this file never classes a number or a model.
  Pages of numbers are POSTed: they are audited, and the filters that select them travel in the body.
*/

export interface PlanningRun {
  /** Unix milliseconds when planned: well inside the range a JSON number carries exactly. */
  runId: number
  asOfSequence: number
  asOfDate: string | null
  publishedAt: string
  /** Only the two newest runs keep their per-number rows; older ones keep their figures for the trend. */
  hasNumbers: boolean
}

export interface PlanningStatus {
  deployed: boolean
  runs: PlanningRun[]
  running: { runId: number; asOfSequence: number; asOfDate: string | null; chunksDone: number; chunksTotal: number } | null
  stale: string | null
  catalogue: { tacVersionId: number; parserVersion: number; models: number; unparsedModels: number; volteMentions: number } | null
}

export interface PlanningClass {
  class: string
  label: string
  numbers: number
  bindings: number
  /** Distinct IMEIs: estimated. */
  handsets: number
  /** Of every number with an active binding. */
  sharePercent: number
}

export interface PlanningBreakdownRow {
  key: string
  label: string
  tac: string | null
  numbers: number
  bindings: number
  handsets: number
}

export interface PlanningTrendPoint {
  asOfSequence: number
  asOfDate: string | null
  class: string
  numbers: number
  bindings: number
}

export interface PlanningModelTrendPoint {
  sequence: number
  date: string | null
  class: string
  bindings: number
}

export interface PlanningNoData {
  topic: string
  reason: string
}

export interface SunsetView {
  run: PlanningRun
  filtered: boolean
  totalNumbers: number
  classes: PlanningClass[]
  notAssessable: PlanningClass[]
  exposedGsmOnly: number
  exposedIot: number
  paired: number
  trend: PlanningTrendPoint[]
  modelTrend: PlanningModelTrendPoint[]
  noData: PlanningNoData[]
}

export interface EsimView {
  run: PlanningRun
  filtered: boolean
  totalNumbers: number
  classes: PlanningClass[]
  trend: PlanningTrendPoint[]
  modelTrend: PlanningModelTrendPoint[]
  noData: PlanningNoData[]
}

export interface IotView {
  run: PlanningRun
  devices: PlanningClass
  byDeviceType: PlanningBreakdownRow[]
  exposure: PlanningBreakdownRow[]
  simRotation: PlanningBreakdownRow[]
  notClassified: PlanningClass
  noData: PlanningNoData[]
}

/** One band and the population of the models whose GSMA record lists it. */
export interface PlanningBandRow {
  technology: 'lte' | 'nr'
  /** 3 for LTE band 3, 78 for n78. */
  band: number
  /** On the operator's configured list. */
  operator: boolean
  numbers: number
  bindings: number
  handsets: number
  sharePercent: number
}

/** N2. Per model: a number holding two models is counted under each, and the figures are estimated. */
export interface BandsView {
  run: PlanningRun
  filtered: boolean
  totalNumbers: number
  /** Empty when the operator's list is not configured: then nothing is matched. */
  operatorLte: number[]
  operatorNr: number[]
  lteMatch: PlanningClass[]
  nrMatch: PlanningClass[]
  architecture: PlanningClass[]
  ambiguous: PlanningClass
  lteBands: PlanningBandRow[]
  nrBands: PlanningBandRow[]
  noData: PlanningNoData[]
}

/** N6. Per model, like N2. */
export interface NtnView {
  run: PlanningRun
  filtered: boolean
  totalNumbers: number
  classes: PlanningClass[]
  byMode: PlanningBreakdownRow[]
  byOrbit: PlanningBreakdownRow[]
  byBand: PlanningBreakdownRow[]
  notClassified: PlanningClass
  modelTrend: PlanningModelTrendPoint[]
  noData: PlanningNoData[]
}

/** N4. Changes and ages are exact counts; the cohorts' shares are Kaplan-Meier estimates. */
export interface LifecycleView {
  run: PlanningRun
  filtered: boolean
  firstMonth: string | null
  lastMonth: string | null
  changes: number
  kinds: { kind: 'newer' | 'older' | 'not-stated' | 'same-model'; label: string; changes: number; sharePercent: number }[]
  vendorMoves: LifecycleMove[]
  modelMoves: LifecycleMove[]
  /** Unfiltered only: a spell carries no model. */
  cohorts: LifecycleCohort[]
  /** Spells already running in the initial dump: their start is unknown. */
  dump: LifecycleCohort | null
  notAssessedNumbers: number
  deviceAge: LifecycleAge[]
  modelAge: LifecycleAge[]
  noData: PlanningNoData[]
}

export interface LifecycleMove {
  from: string
  to: string
  changes: number
  /** To a model with a later GSMA allocation date. */
  upgrades: number
}

export interface LifecycleCohort {
  cohort: string
  spells: number
  changed: number
  censored: number
  /** Percent; null where nobody in the cohort was followed that long. */
  changedBy30: number | null
  changedBy90: number | null
  changedBy180: number | null
  upgradedBy90: number | null
  upgradedBy180: number | null
  medianDays: number | null
}

export interface LifecycleAge {
  key: string
  label: string
  handsets: number
  sharePercent: number
}

export interface PlanningBreakdown {
  run: PlanningRun
  view: string
  class: string
  by: string
  rows: PlanningBreakdownRow[]
  total: number
}

export interface PlanningNumberRow {
  /** Masked without identifier.reveal. */
  msisdn: string
  reason: string | null
  handsets: number
  bindings: number
  models: string[]
}

export interface PlanningNumberPage {
  run: PlanningRun
  class: string
  rows: PlanningNumberRow[]
  total: number
  page: number
  pageSize: number
  masked: boolean
}

export interface PlanningFilter {
  vendor?: string
  model?: string
  deviceType?: string
}

export type PlanningView = 'sunset' | 'esim' | 'iot' | 'bands' | 'ntn'
export type PlanningBy = 'vendor' | 'model' | 'deviceType' | 'tac'

function params(run: number | null, filter: PlanningFilter) {
  return { run: run ?? undefined, vendor: filter.vendor, model: filter.model, deviceType: filter.deviceType }
}

export function usePlanningStatus() {
  return useQuery({
    queryKey: ['planning', 'status'],
    queryFn: ({ signal }) => api.get<PlanningStatus>('/api/v1/planning/status', undefined, signal),
    staleTime: 60_000,
  })
}

function useView<T>(view: PlanningView | 'lifecycle', run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) {
  return useQuery({
    queryKey: computed(() => ['planning', view, toValue(run), toValue(filter)]),
    queryFn: ({ signal }) => api.get<T>(`/api/v1/planning/${view}`, params(toValue(run), toValue(filter)), signal),
    enabled: computed(() => toValue(enabled)),
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  })
}

export const useSunset = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<SunsetView>('sunset', run, filter, enabled)

export const useEsim = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<EsimView>('esim', run, filter, enabled)

export const useIot = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<IotView>('iot', run, filter, enabled)

export const useLifecycle = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<LifecycleView>('lifecycle', run, filter, enabled)

export const useBands = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<BandsView>('bands', run, filter, enabled)

export const useNtn = (run: MaybeRefOrGetter<number | null>, filter: MaybeRefOrGetter<PlanningFilter>, enabled: MaybeRefOrGetter<boolean>) =>
  useView<NtnView>('ntn', run, filter, enabled)

export function usePlanningBreakdown(
  view: MaybeRefOrGetter<PlanningView>,
  planningClass: MaybeRefOrGetter<string>,
  by: MaybeRefOrGetter<PlanningBy>,
  run: MaybeRefOrGetter<number | null>,
  filter: MaybeRefOrGetter<PlanningFilter>,
  enabled: MaybeRefOrGetter<boolean>,
) {
  return useQuery({
    queryKey: computed(() => ['planning', 'breakdown', toValue(view), toValue(planningClass), toValue(by), toValue(run), toValue(filter)]),
    queryFn: ({ signal }) =>
      api.get<PlanningBreakdown>(
        '/api/v1/planning/breakdown',
        { view: toValue(view), class: toValue(planningClass), by: toValue(by), limit: 50, ...params(toValue(run), toValue(filter)) },
        signal,
      ),
    enabled: computed(() => toValue(enabled)),
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  })
}

export function usePlanningBreakdownExport() {
  return useMutation({
    mutationFn: (request: { view: PlanningView; class: string; by: PlanningBy; runId: number | null } & PlanningFilter) =>
      api.download('/api/v1/planning/breakdown/export', request),
  })
}

export function usePlanningNumbers() {
  return useMutation({
    mutationFn: (request: { view: 'sunset' | 'esim'; class: string; reason?: string | null; runId: number | null; page: number; pageSize: number } & PlanningFilter) =>
      api.post<PlanningNumberPage>('/api/v1/planning/numbers', request),
  })
}
