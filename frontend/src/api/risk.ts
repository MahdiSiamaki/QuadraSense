import { useMutation, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'

/*
  Risk signals. Mirrors Sqm.Contracts.Risk.

  Every level and every reason is written by the server, from the configured rule set, and shown
  here as it arrives - this file never decides a level. Lists and entities are POSTed: the entity's
  identifier travels in the body, and a list is audited, so neither belongs in a cache that re-runs
  it on focus.
*/

export type RiskLevel = 'Observation' | 'Anomaly' | 'RiskSignal' | 'SuspiciousPattern' | 'ConfirmedFraud'
export type RiskFamily = 'Imei' | 'Sim' | 'Number'
export type RiskListKind = 'Sims' | 'ImeiWindow' | 'ImeiLifetime' | 'Numbers'
export type RiskView = 'risk' | 'dataQuality'

export interface RiskRule {
  rule: string
  family: RiskFamily
  list: RiskListKind
  unit: string
  what: string
  windowDays: number | null
  windowFrom: string | null
  windowTo: string | null
  daysWithData: number | null
  /** More than this is out of line. Null until calibrated. */
  threshold: number | null
  tacsThreshold: number | null
  /** The smallest value stored; a threshold under floor - 1 is refused. */
  floor: number
  /** The highest level the rule can give. */
  ceiling: RiskLevel
  cappingChecks: string[]
  /** Days in the window flagged for one of the capping checks: results are held at Anomaly. */
  flaggedDays: string[]
  hasDataQualityView: boolean
}

export interface RiskStatus {
  ready: boolean
  notReady: string | null
  calibrated: boolean
  ruleSetVersion: string
  maxDefectShare: number | null
  dataThrough: string | null
  run: { asOf: string; publishedAt: string; stale: string | null } | null
  rules: RiskRule[]
  /** GSMA device types IMEI and SIM lists can be narrowed to. */
  deviceTypes: string[]
}

export interface RiskOverviewRule {
  rule: string
  level: RiskLevel
  capped: boolean
  listed: number
  dataQuality: number
}

export interface RiskOverview {
  ruleSetVersion: string
  asOf: string
  rules: RiskOverviewRule[]
}

export interface RiskReason {
  rule: string
  level: RiskLevel
  value: number
  threshold: number | null
  capped: boolean
  text: string
}

export interface RiskColumn {
  name: string
  label: string
  type: 'Number' | 'Date' | 'Text' | 'Tags'
}

export interface RiskRow {
  /** Masked without identifier.reveal. */
  key: string
  drillable: boolean
  level: RiskLevel
  assessable: boolean
  reasons: RiskReason[]
  values: Record<string, number | string | string[] | null>
}

export interface RiskListRequest {
  rule: string
  view?: RiskView
  threshold?: number | null
  tacsThreshold?: number | null
  page?: number
  pageSize?: number
  /** For IMEI lists the handset's own type; for SIM lists the type of the SIM's most frequent TAC. */
  deviceTypes?: string[]
}

export interface RiskListResult {
  rule: string
  view: RiskView
  kind: 'msisdn' | 'imsi' | 'imei'
  threshold: number
  tacsThreshold: number | null
  overridden: boolean
  columns: RiskColumn[]
  rows: RiskRow[]
  total: number
  reachable: number
  page: number
  pageSize: number
  masked: boolean
  ruleSetVersion: string
  asOf: string
  elapsedMs: number
}

export interface RiskEntity {
  kind: 'msisdn' | 'imsi' | 'imei'
  family: RiskFamily
  /** False when every measure is below its storage floor: nothing stored, nothing out of line. */
  stored: boolean
  level: RiskLevel
  assessable: boolean
  reasons: RiskReason[]
  values: Record<string, number | string | string[] | null>
  columns: RiskColumn[]
  ruleSetVersion: string
  asOf: string
  /** With the entities bound to it in the 30 days: SuspiciousPattern when two families signal. */
  pattern: RiskLevel
  /** Per family, how many bound entities have stored measures, and at which levels. Names nobody. */
  linked: { family: RiskFamily; stored: number; riskSignals: number; anomalies: number }[]
}

export function useRiskStatus(enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: ['risk', 'status'],
    queryFn: ({ signal }) => api.get<RiskStatus>('/api/v1/risk/status', undefined, signal),
    enabled: computed(() => toValue(enabled)),
    staleTime: 60_000,
  })
}

/** Counts per rule. Names nobody, so it can be cached like any aggregate. */
export function useRiskOverview(enabled: MaybeRefOrGetter<boolean>) {
  return useQuery({
    queryKey: ['risk', 'overview'],
    queryFn: ({ signal }) => api.get<RiskOverview>('/api/v1/risk/overview', undefined, signal),
    enabled: computed(() => toValue(enabled)),
    staleTime: 60_000,
  })
}

export function useRiskList() {
  return useMutation({
    mutationFn: (request: RiskListRequest) => api.post<RiskListResult>('/api/v1/risk/list', request),
  })
}

export function useRiskExport() {
  return useMutation({
    mutationFn: (request: RiskListRequest) => api.download('/api/v1/risk/export', request),
  })
}

export function useRiskEntity() {
  return useMutation({
    mutationFn: (identifier: string) => api.post<RiskEntity>('/api/v1/risk/entity', { identifier }),
  })
}

/** Words for a level. "Suspected" is in the pattern's name because it is never more than that. */
export const LEVEL_LABELS: Record<RiskLevel, string> = {
  Observation: 'Observation',
  Anomaly: 'Anomaly',
  RiskSignal: 'Risk signal',
  SuspiciousPattern: 'Suspected pattern',
  ConfirmedFraud: 'Confirmed (by a person)',
}

/** Short titles for the rules, naming their unit. */
export const RULE_TITLES: Record<string, string> = {
  SharedImeiSims30: 'SIMs added to one IMEI, 30 days',
  SharedImeiNumbers30: 'Numbers added to one IMEI, 30 days',
  SharedImeiSimsEver: 'SIMs ever on one IMEI',
  SharedImeiSimsNotRemoved: 'SIMs not yet removed from one IMEI',
  HighDeviceCount30: 'IMEIs one SIM was added to, 30 days',
  RapidDeviceChange7: 'IMEIs one SIM was added to, 7 days',
  Randomisation20: 'IMEIs and TACs one SIM was added to, 20 days',
  RepeatedSimChange7: 'Days with a SIM change on one number, 7 days',
}

/** Words for the feed-quality checks. */
export const CHECK_LABELS: Record<string, string> = {
  ShiftedImei: 'shifted IMEIs',
  MultiNumberSim: 'SIMs with several numbers',
  MalformedImei: 'malformed IMEIs',
  UnknownDevice: 'unknown devices',
}
