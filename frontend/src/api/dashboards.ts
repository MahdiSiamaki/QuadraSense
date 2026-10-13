import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'
import type { DashboardDefinition, DashboardLens } from '@/features/dashboard/types/definition'
import type { WidgetSnapshotData } from '@/features/dashboard/types/widget'

/*
  The dashboard builder's metadata API (ADR-016), under /api/v1/dashboards - plural, beside the
  analytics routes under /api/v1/dashboard. These types mirror Sqm.Contracts.Dashboards.

  Server state lives here, in TanStack Query, and only here: the edit draft is a copy taken when
  editing starts (features/dashboard/composables/useDashboardDraft.ts), never a second cache.
*/

export type DashboardAccessLevel = 'owner' | 'manager' | 'editor' | 'viewer' | 'none'

export interface DashboardOwnerInfo {
  id: number
  displayName: string
}

export interface DashboardAccessInfo {
  level: DashboardAccessLevel
  canEdit: boolean
  canShare: boolean
  canDelete: boolean
  canDuplicate: boolean
  canExportData: boolean
}

export interface DashboardSummary {
  id: number
  name: string
  description: string
  owner: DashboardOwnerInfo
  access: DashboardAccessInfo
  isShared: boolean
  isFavorite: boolean
  isDefault: boolean
  revision: number
  widgetCount: number
  updatedAt: string
}

export interface DashboardDefaultInfo {
  dashboardId: number | null
  templateKey: string | null
}

export interface DashboardLimits {
  maxWidgets: number
  maxNameLength: number
  maxDescriptionLength: number
  maxTitleLength: number
  maxSubtitleLength: number
  maxWidgetConfigBytes: number
  maxDefinitionBytes: number
  maxGrants: number
}

export interface DashboardList {
  mine: DashboardSummary[]
  sharedWithMe: DashboardSummary[]
  /** Everybody else's, for dashboard.manage when asked with scope=all; null otherwise. */
  others: DashboardSummary[] | null
  default: DashboardDefaultInfo
  limits: DashboardLimits
}

export interface DashboardDetail {
  id: number
  name: string
  description: string
  owner: DashboardOwnerInfo
  definition: DashboardDefinition
  schemaVersion: number
  revision: number
  sourceTemplate: string | null
  createdAt: string
  updatedAt: string
  updatedBy: string | null
  access: DashboardAccessInfo
  isFavorite: boolean
  isDefault: boolean
  grantCount: number
}

export interface DashboardGrant {
  id: number
  subjectType: 'user' | 'role'
  subjectId: number
  subjectName: string
  subjectDetail: string | null
  level: 'viewer' | 'editor'
  grantedAt: string
  grantedBy: string | null
}

export interface DashboardGrants {
  grants: DashboardGrant[]
  maxGrants: number
}

export interface ShareSubject {
  type: 'user' | 'role'
  id: number
  name: string
  detail: string | null
}

export interface ShareDirectory {
  users: ShareSubject[]
  roles: ShareSubject[]
}

export interface WidgetTypeAvailability {
  type: string
  configVersion: number
  available: boolean
  missingPermissions: string[]
  /** For a type whose permission follows a config field: the values the caller may choose. */
  availableOptions: string[] | null
}

export interface WidgetTypes {
  contractVersion: number
  types: WidgetTypeAvailability[]
}

/** A dashboard's structure as a file. No results, owner, grants or preferences. */
export interface DashboardConfigDocument {
  format: 'quadrasense.dashboard'
  formatVersion: 1
  exportedAt: string
  name: string
  description: string
  definition: DashboardDefinition
}

export interface DashboardSnapshot {
  dashboardId: number | null
  name: string
  preparedAt: string
  lens: DashboardLens
  widgets: WidgetSnapshotData[]
}

/** The 409 an update gets when somebody saved first. */
export interface DashboardConflictInfo {
  currentRevision: number
  updatedAt: string
  updatedBy: string | null
}

const ROOT = '/api/v1/dashboards'
export const DASHBOARDS_KEY = ['dashboards'] as const

const listKey = (scope: 'mine' | 'all') => [...DASHBOARDS_KEY, 'list', scope] as const
const detailKey = (id: number) => [...DASHBOARDS_KEY, 'detail', id] as const
const grantsKey = (id: number) => [...DASHBOARDS_KEY, 'grants', id] as const

export function useDashboardList(scope: MaybeRefOrGetter<'mine' | 'all'> = 'mine') {
  return useQuery({
    queryKey: computed(() => listKey(toValue(scope))),
    queryFn: ({ signal }) =>
      api.get<DashboardList>(ROOT, toValue(scope) === 'all' ? { scope: 'all' } : undefined, signal),
    staleTime: 30_000,
  })
}

export function useDashboard(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: computed(() => detailKey(toValue(id) ?? -1)),
    queryFn: ({ signal }) => api.get<DashboardDetail>(`${ROOT}/${toValue(id)}`, undefined, signal),
    enabled: computed(() => toValue(id) !== null),
    staleTime: 30_000,
    // A missing or forbidden dashboard answers 404, and asking again will not change that.
    retry: false,
  })
}

export interface CreateDashboardInput {
  name: string
  description?: string
  definition: DashboardDefinition
  sourceTemplate?: string | null
}

export function useCreateDashboard() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: CreateDashboardInput) => api.post<DashboardDetail>(ROOT, input),
    onSuccess: (created) => {
      client.setQueryData(detailKey(created.id), created)
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export interface UpdateDashboardInput {
  id: number
  name: string
  description: string
  definition: DashboardDefinition
  expectedRevision: number
}

export function useUpdateDashboard() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...body }: UpdateDashboardInput) => api.put<DashboardDetail>(`${ROOT}/${id}`, body),
    onSuccess: (saved) => {
      client.setQueryData(detailKey(saved.id), saved)
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export function useDeleteDashboard() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`${ROOT}/${id}`),
    onSuccess: (_, id) => {
      client.removeQueries({ queryKey: detailKey(id) })
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export function useDuplicateDashboard() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name }: { id: number; name?: string }) =>
      api.post<DashboardDetail>(`${ROOT}/${id}/duplicate`, { name: name ?? null }),
    onSuccess: (created) => {
      client.setQueryData(detailKey(created.id), created)
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export function useDashboardGrants(id: MaybeRefOrGetter<number | null>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => grantsKey(toValue(id) ?? -1)),
    queryFn: ({ signal }) => api.get<DashboardGrants>(`${ROOT}/${toValue(id)}/grants`, undefined, signal),
    enabled: computed(() => toValue(id) !== null && toValue(enabled)),
    staleTime: 0,
  })
}

function useGrantMutation<T>(fn: (input: T) => Promise<unknown>, idOf: (input: T) => number) {
  const client = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (_, input) => {
      const id = idOf(input)
      void client.invalidateQueries({ queryKey: grantsKey(id) })
      void client.invalidateQueries({ queryKey: detailKey(id) })
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export function useAddGrant() {
  return useGrantMutation(
    (input: { dashboardId: number; subjectType: 'user' | 'role'; subjectId: number; level: 'viewer' | 'editor' }) =>
      api.post<DashboardGrant>(`${ROOT}/${input.dashboardId}/grants`, {
        subjectType: input.subjectType,
        subjectId: input.subjectId,
        level: input.level,
      }),
    (input) => input.dashboardId,
  )
}

export function useUpdateGrant() {
  return useGrantMutation(
    (input: { dashboardId: number; grantId: number; level: 'viewer' | 'editor' }) =>
      api.put<DashboardGrant>(`${ROOT}/${input.dashboardId}/grants/${input.grantId}`, { level: input.level }),
    (input) => input.dashboardId,
  )
}

export function useRemoveGrant() {
  return useGrantMutation(
    (input: { dashboardId: number; grantId: number }) =>
      api.delete<void>(`${ROOT}/${input.dashboardId}/grants/${input.grantId}`),
    (input) => input.dashboardId,
  )
}

/** Users and roles a dashboard can be shared with. Never email or login data. */
export function useShareDirectory(term: MaybeRefOrGetter<string>, enabled: MaybeRefOrGetter<boolean> = true) {
  return useQuery({
    queryKey: computed(() => [...DASHBOARDS_KEY, 'share-directory', toValue(term).trim()] as const),
    queryFn: ({ signal }) =>
      api.get<ShareDirectory>(`${ROOT}/share-directory`, { q: toValue(term).trim() }, signal),
    enabled: computed(() => toValue(enabled)),
    staleTime: 30_000,
    placeholderData: (previous) => previous,
  })
}

export function useSetFavorite() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, favorite }: { id: number; favorite: boolean }) =>
      favorite ? api.put<void>(`${ROOT}/${id}/favorite`, {}) : api.delete<void>(`${ROOT}/${id}/favorite`),
    onSuccess: (_, { id }) => {
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
      void client.invalidateQueries({ queryKey: detailKey(id) })
    },
  })
}

export function useSetDefault() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { dashboardId: number | null; templateKey: string | null }) =>
      api.put<void>(`${ROOT}/preferences/default`, input),
    onSuccess: () => client.invalidateQueries({ queryKey: DASHBOARDS_KEY }),
  })
}

/** Which widget types the caller may use. Decided on the server; the catalogue only reflects it. */
export function useWidgetTypes() {
  return useQuery({
    queryKey: [...DASHBOARDS_KEY, 'widget-types'],
    queryFn: ({ signal }) => api.get<WidgetTypes>(`${ROOT}/widget-types`, undefined, signal),
    staleTime: 60_000,
  })
}

export function exportDashboardConfig(id: number, signal?: AbortSignal) {
  return api.get<DashboardConfigDocument>(`${ROOT}/${id}/config`, undefined, signal)
}

export function useImportDashboard() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { document: DashboardConfigDocument; name?: string | null }) =>
      api.post<DashboardDetail>(`${ROOT}/import`, { document: input.document, name: input.name ?? null }),
    onSuccess: (created) => {
      client.setQueryData(detailKey(created.id), created)
      void client.invalidateQueries({ queryKey: [...DASHBOARDS_KEY, 'list'] })
    },
  })
}

export interface SnapshotRequest {
  dashboardId: number | null
  name: string
  definition: DashboardDefinition
  lens: DashboardLens
  widgetIds?: string[] | null
  purpose: 'png' | 'print'
}

/**
 * The server prepares and audits an image or print export: it checks access, data.export and each
 * widget's own permission, and loads each permitted aggregate itself (ADR-016 §11).
 */
export function prepareSnapshot(request: SnapshotRequest, signal?: AbortSignal) {
  return api.post<DashboardSnapshot>(`${ROOT}/export/snapshot`, request, signal)
}

export interface CsvRequest {
  dashboardId: number | null
  name: string
  definition: DashboardDefinition
  lens: DashboardLens
  widgetIds: string[]
}

/** One widget as CSV, several as a ZIP - written on the server with formula neutralisation. */
export function downloadWidgetCsv(request: CsvRequest, signal?: AbortSignal) {
  return api.download(`${ROOT}/export/csv`, request, signal)
}
