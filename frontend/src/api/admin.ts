import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from './client'
import type { SessionInfo } from './auth'

/*
  Users, roles, permissions and the audit log.

  Mirrors Sqm.Application.Identity. The one shape worth reading carefully is PermissionResolution:
  it carries not just whether a user holds a permission but WHY, because "why can she export?"
  and "I removed her lookup access, why does she still have it?" are the only two questions
  anyone ever asks of an access-control screen.
*/

export interface RoleRef {
  id: number
  code: string
  displayName: string
}

export interface UserSummary {
  id: number
  username: string
  displayName: string
  email: string | null
  jobTitle: string | null
  provider: string
  isActive: boolean
  /** Temporarily locked out by failed sign-ins. Distinct from deactivated. */
  isLocked: boolean
  lastLoginAt: string | null
  createdAt: string
  roles: RoleRef[]
}

export interface UserPage {
  items: UserSummary[]
  total: number
  page: number
  pageSize: number
}

/** One permission, and the complete account of why this user does or does not have it. */
export interface PermissionResolution {
  code: string
  category: string
  displayName: string
  description: string
  /** Shown with a warning when granting. Never a security control. */
  isDangerous: boolean
  isGranted: boolean
  /** Display names of the roles carrying it. Empty when none do. */
  grantedByRoles: string[]
  grantedDirectly: boolean
  /** Overrides everything above. */
  deniedDirectly: boolean
  overrideReason: string | null
}

export interface UserDetail {
  id: number
  username: string
  displayName: string
  email: string | null
  jobTitle: string | null
  phone: string | null
  provider: string
  isActive: boolean
  mustChangePassword: boolean
  lastLoginAt: string | null
  previousLoginAt: string | null
  lastLoginIp: string | null
  failedLoginCount: number
  lockedUntil: string | null
  passwordUpdatedAt: string | null
  deactivatedAt: string | null
  deactivatedBy: string | null
  deactivationReason: string | null
  createdAt: string
  createdBy: string | null
  updatedAt: string
  updatedBy: string | null
  roles: RoleRef[]
  /** Every permission in the catalogue, granted or not. */
  permissions: PermissionResolution[]
  activeSessions: number
}

export interface PermissionDefinition {
  code: string
  category: string
  displayName: string
  description: string
  isDangerous: boolean
  sortOrder: number
}

export interface RoleDetail {
  id: number
  code: string
  displayName: string
  description: string
  /** Built-in. Cannot be deleted or renamed by code; its permissions remain editable. */
  isSystem: boolean
  memberCount: number
  permissionCodes: string[]
  createdAt: string
  createdBy: string | null
  updatedAt: string
  updatedBy: string | null
}

export interface AuditRecord {
  entryId: string
  occurredAt: string
  actorUserId: number | null
  actorName: string
  action: string
  category: string
  outcome: 'success' | 'failure' | 'denied'
  targetType: string | null
  targetId: string | null
  targetName: string | null
  sourceIp: string | null
  correlationId: string | null
  /** Raw JSON, rendered on demand. */
  detail: string | null
}

export interface AuditPage {
  items: AuditRecord[]
  total: number
  page: number
  pageSize: number
}

export interface UserFilters {
  search?: string
  role?: string
  active?: boolean
  sort?: 'displayName' | 'lastLogin' | 'created'
  page?: number
  pageSize?: number
}

export interface AuditFilters {
  search?: string
  action?: string
  category?: string
  outcome?: string
  actorUserId?: number
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

// ---------------------------------------------------------------- users

export function useUsers(filters: MaybeRefOrGetter<UserFilters>) {
  return useQuery({
    queryKey: ['users', computed(() => toValue(filters))],
    queryFn: ({ signal }) =>
      api.get<UserPage>('/api/v1/users/', { ...toValue(filters) }, signal),
    staleTime: 15_000,
  })
}

export function useUser(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: ['user', computed(() => toValue(id))],
    queryFn: ({ signal }) =>
      api.get<UserDetail>(`/api/v1/users/${toValue(id)}`, undefined, signal),
    enabled: computed(() => toValue(id) !== null),
  })
}

export function useUserSessions(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: ['user-sessions', computed(() => toValue(id))],
    queryFn: ({ signal }) =>
      api.get<SessionInfo[]>(`/api/v1/users/${toValue(id)}/sessions`, undefined, signal),
    enabled: computed(() => toValue(id) !== null),
  })
}

/**
 * Invalidates everything a change to one user can affect.
 *
 * Roles carry member counts and the user list shows role chips, so a role assignment changes
 * three screens. Invalidating each explicitly is more code than clearing the cache and far less
 * disruptive: clearing would blank the dashboard behind a modal.
 */
function invalidateUser(client: ReturnType<typeof useQueryClient>, id?: number) {
  client.invalidateQueries({ queryKey: ['users'] })
  client.invalidateQueries({ queryKey: ['roles'] })
  if (id !== undefined) {
    client.invalidateQueries({ queryKey: ['user', id] })
    client.invalidateQueries({ queryKey: ['user-sessions', id] })
  }
  client.invalidateQueries({ queryKey: ['audit'] })
}

export function useCreateUser() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (user: {
      username: string
      displayName: string
      password: string
      email: string | null
      jobTitle: string | null
      phone: string | null
      roleCodes: string[]
      mustChangePassword: boolean
    }) => api.post<UserDetail>('/api/v1/users/', user),
    onSuccess: () => invalidateUser(client),
  })
}

export function useUpdateUser() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      id: number
      displayName: string
      email: string | null
      jobTitle: string | null
      phone: string | null
    }) => api.put<void>(`/api/v1/users/${input.id}`, input),
    onSuccess: (_, input) => invalidateUser(client, input.id),
  })
}

export function useSetUserActive() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { id: number; isActive: boolean; reason: string | null }) =>
      api.put<void>(`/api/v1/users/${input.id}/active`, input),
    onSuccess: (_, input) => invalidateUser(client, input.id),
  })
}

export function useSetUserRoles() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { id: number; roleCodes: string[] }) =>
      api.put<void>(`/api/v1/users/${input.id}/roles`, { roleCodes: input.roleCodes }),
    onSuccess: (_, input) => invalidateUser(client, input.id),
  })
}

export function useSetUserPermissions() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: {
      id: number
      overrides: Array<{ permissionCode: string; effect: 'grant' | 'deny'; reason: string | null }>
    }) => api.put<void>(`/api/v1/users/${input.id}/permissions`, { overrides: input.overrides }),
    onSuccess: (_, input) => invalidateUser(client, input.id),
  })
}

export function useResetPassword() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { id: number; newPassword: string }) =>
      api.post<void>(`/api/v1/users/${input.id}/password`, { newPassword: input.newPassword }),
    onSuccess: (_, input) => invalidateUser(client, input.id),
  })
}

export function useUnlockUser() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.post<void>(`/api/v1/users/${id}/unlock`, {}),
    onSuccess: (_, id) => invalidateUser(client, id),
  })
}

export function useRevokeUserSessions() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) =>
      api.post<{ revoked: number }>(`/api/v1/users/${id}/revoke-sessions`, {}),
    onSuccess: (_, id) => invalidateUser(client, id),
  })
}

// ---------------------------------------------------------------- roles

export function useRoles() {
  return useQuery({
    queryKey: ['roles'],
    queryFn: ({ signal }) => api.get<RoleDetail[]>('/api/v1/roles/', undefined, signal),
    staleTime: 60_000,
  })
}

export function useRole(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: ['role', computed(() => toValue(id))],
    queryFn: ({ signal }) =>
      api.get<RoleDetail>(`/api/v1/roles/${toValue(id)}`, undefined, signal),
    enabled: computed(() => toValue(id) !== null),
  })
}

export function useRoleMembers(id: MaybeRefOrGetter<number | null>) {
  return useQuery({
    queryKey: ['role-members', computed(() => toValue(id))],
    queryFn: ({ signal }) =>
      api.get<UserSummary[]>(`/api/v1/roles/${toValue(id)}/members`, undefined, signal),
    enabled: computed(() => toValue(id) !== null),
  })
}

export function usePermissionCatalogue() {
  return useQuery({
    queryKey: ['permissions'],
    queryFn: ({ signal }) =>
      api.get<PermissionDefinition[]>('/api/v1/permissions', undefined, signal),
    // The catalogue only changes with a migration, so it is effectively static within a session.
    staleTime: Infinity,
  })
}

function invalidateRoles(client: ReturnType<typeof useQueryClient>, id?: number) {
  client.invalidateQueries({ queryKey: ['roles'] })
  client.invalidateQueries({ queryKey: ['users'] })
  client.invalidateQueries({ queryKey: ['audit'] })
  if (id !== undefined) {
    client.invalidateQueries({ queryKey: ['role', id] })
    client.invalidateQueries({ queryKey: ['role-members', id] })
  }
}

export function useCreateRole() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (role: {
      code: string
      displayName: string
      description: string
      permissionCodes: string[]
    }) => api.post<RoleDetail>('/api/v1/roles/', role),
    onSuccess: () => invalidateRoles(client),
  })
}

export function useUpdateRole() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { id: number; displayName: string; description: string }) =>
      api.put<void>(`/api/v1/roles/${input.id}`, input),
    onSuccess: (_, input) => invalidateRoles(client, input.id),
  })
}

export function useSetRolePermissions() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (input: { id: number; permissionCodes: string[] }) =>
      api.put<void>(`/api/v1/roles/${input.id}/permissions`, {
        permissionCodes: input.permissionCodes,
      }),
    onSuccess: (_, input) => {
      invalidateRoles(client, input.id)
      // A role's permissions changing can change the signed-in user's own set.
      client.invalidateQueries({ queryKey: ['auth', 'me'] })
    },
  })
}

export function useDeleteRole() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/v1/roles/${id}`),
    onSuccess: () => invalidateRoles(client),
  })
}

// ---------------------------------------------------------------- audit

export function useAuditLog(filters: MaybeRefOrGetter<AuditFilters>) {
  return useQuery({
    queryKey: ['audit', computed(() => toValue(filters))],
    queryFn: ({ signal }) => api.get<AuditPage>('/api/v1/audit/', { ...toValue(filters) }, signal),
    staleTime: 10_000,
  })
}

export function useAuditActions() {
  return useQuery({
    queryKey: ['audit-actions'],
    queryFn: ({ signal }) => api.get<string[]>('/api/v1/audit/actions', undefined, signal),
    staleTime: 5 * 60_000,
  })
}
