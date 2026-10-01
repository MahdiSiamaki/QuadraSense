import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from './client'

/*
  The Explorer.

  Every call that carries a query is a POST: a query's values can be numbers, SIMs or handsets,
  and a query string reaches access logs, browser history and Referer headers. The same rule
  keeps identifiers out of this page's own URL - drill-down happens in a panel, not a route.
*/

export type ExplorerDataset = 'Bindings' | 'Events'

export type ExplorerOperator =
  | 'Equals'
  | 'NotEquals'
  | 'GreaterThan'
  | 'GreaterOrEqual'
  | 'LessThan'
  | 'LessOrEqual'
  | 'In'
  | 'NotIn'
  | 'Contains'
  | 'StartsWith'
  | 'Between'
  | 'IsNull'
  | 'IsNotNull'

export type ExplorerFieldType =
  | 'Msisdn'
  | 'Imsi'
  | 'Imei'
  | 'Tac'
  | 'Text'
  | 'Date'
  | 'Boolean'
  | 'Label'
  | 'Number'

export type ExplorerAggregate = 'Count' | 'CountDistinct' | 'Min' | 'Max'

/** One node of a condition tree: a group (logic + children) or a condition (field + operator + values). */
export interface ExplorerFilter {
  logic?: 'And' | 'Or' | null
  children?: ExplorerFilter[] | null
  field?: string | null
  operator?: ExplorerOperator | null
  values?: string[] | null
  not?: boolean
}

export interface ExplorerMeasure {
  name: string
  aggregate: ExplorerAggregate
  field?: string | null
  activeOnly?: boolean
}

export interface ExplorerSort {
  field: string
  descending?: boolean
}

export interface ExplorerQueryRequest {
  dataset: ExplorerDataset
  where?: ExplorerFilter | null
  columns?: string[] | null
  groupBy?: string[] | null
  measures?: ExplorerMeasure[] | null
  having?: ExplorerFilter | null
  sort?: ExplorerSort[] | null
  page?: number
  pageSize?: number
}

export type PlanVerdict = 'Light' | 'Moderate' | 'Heavy' | 'Refused'

export interface ExplorerPlan {
  source: string
  /** KeyRead, RangeRead, IndexRead or Scan. */
  access: string
  estimatedRows: number
  verdict: PlanVerdict
  budgetRows: number
  notes: string[]
}

export interface ExplorerColumn {
  name: string
  label: string
  type: ExplorerFieldType
  /** Redacted by the server: the caller lacks identifier.reveal. */
  masked: boolean
}

export type ExplorerValue = string | number | boolean | null

export interface ExplorerResult {
  columns: ExplorerColumn[]
  rows: ExplorerValue[][]
  total: number
  /** How many of `total` paging can reach. */
  reachable: number
  page: number
  pageSize: number
  plan: ExplorerPlan
  elapsedMs: number
  rowsRead: number
}

export interface ExplorerField {
  name: string
  label: string
  type: ExplorerFieldType
  operators: ExplorerOperator[]
  groupable: boolean
  /** The lookup permission needed to filter on, show or group by it. */
  permission: string | null
  description: string
  /** For a Label field, the values it may hold. */
  values: string[] | null
}

export interface ExplorerDatasetInfo {
  dataset: ExplorerDataset
  label: string
  description: string
  fields: ExplorerField[]
  /** What a query naming no columns returns, of what the caller may see. */
  defaultColumns: string[]
}

export interface ExplorerCatalogue {
  datasets: ExplorerDatasetInfo[]
  maxPageSize: number
  maxReachableRows: number
  budgetRows: number
  /** The latest day in the event log: what every answer is as of. */
  dataThrough: string | null
}

export interface SavedQuery {
  id: number
  name: string
  description: string
  query: ExplorerQueryRequest
  createdAt: string
  updatedAt: string
}

export interface EntitySummary {
  kind: 'msisdn' | 'imsi' | 'imei' | 'tac'
  identifier: string
  found: boolean
  bindings: number
  activeBindings: number
  numbers: number
  activeNumbers: number
  sims: number
  activeSims: number
  /** Different 14-digit IMEIs - not physical devices: a dual-SIM phone has two. */
  handsets: number
  activeHandsets: number
  lastChange: string | null
  tac: string | null
  brand: string | null
  model: string | null
  plan: ExplorerPlan
}

const SAVED_KEY = ['explorer', 'saved'] as const

export function useExplorerCatalogue() {
  return useQuery({
    queryKey: ['explorer', 'catalogue'],
    queryFn: ({ signal }) => api.get<ExplorerCatalogue>('/api/v1/explorer/catalogue', undefined, signal),
    staleTime: 5 * 60_000,
  })
}

/**
 * Runs a query. A mutation, as the IMSI search is: the user asks for it, it is audited, and it
 * must not re-run by itself when the window regains focus - some cost seconds of server time.
 */
export function useExplorerRun() {
  return useMutation({
    mutationFn: (query: ExplorerQueryRequest) => api.post<ExplorerResult>('/api/v1/explorer/query', query),
  })
}

/** What a query would cost, without running it. */
export function useExplorerPlan() {
  return useMutation({
    mutationFn: (query: ExplorerQueryRequest) => api.post<ExplorerPlan>('/api/v1/explorer/plan', query),
  })
}

/** The query's reachable rows as CSV. */
export function useExplorerExport() {
  return useMutation({
    mutationFn: (query: ExplorerQueryRequest) => api.download('/api/v1/explorer/export', query),
  })
}

/** What current state holds for one identifier. The identifier travels in the body. */
export function useEntitySummary() {
  return useMutation({
    mutationFn: (identifier: string) =>
      api.post<EntitySummary>('/api/v1/explorer/entity', { identifier }),
  })
}

export function useSavedQueries() {
  return useQuery({
    queryKey: SAVED_KEY,
    queryFn: ({ signal }) => api.get<SavedQuery[]>('/api/v1/explorer/saved', undefined, signal),
  })
}

export interface SaveQueryInput {
  id?: number
  name: string
  description: string
  query: ExplorerQueryRequest
}

export function useSaveQuery() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...body }: SaveQueryInput) =>
      id === undefined
        ? api.post<SavedQuery>('/api/v1/explorer/saved', body)
        : api.put<SavedQuery>(`/api/v1/explorer/saved/${id}`, body),
    onSuccess: () => client.invalidateQueries({ queryKey: SAVED_KEY }),
  })
}

export function useDeleteSavedQuery() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/v1/explorer/saved/${id}`),
    onSuccess: () => client.invalidateQueries({ queryKey: SAVED_KEY }),
  })
}
