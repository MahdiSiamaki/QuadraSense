import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query'
import { api } from './client'

/*
  The signed-in user, and everything a person can do about their own account.

  These types mirror Sqm.Api.Endpoints. Like the dashboard types they are a temporary
  hand-written bridge until openapi-typescript generates them.
*/

/** The signed-in user, as the SPA needs them. */
export interface CurrentUser {
  id: number
  username: string
  displayName: string
  email: string | null
  jobTitle: string | null
  phone: string | null
  /**
   * When true the app must show nothing but the change-password screen.
   *
   * Not merely a hint: the API refuses every other endpoint while it is set, so a UI that
   * ignored it would show a wall of access-denied messages.
   */
  mustChangePassword: boolean
  /** The effective set - roles, plus direct grants, minus denies. Resolved server-side. */
  permissions: string[]
  roles: string[]
  lastLoginAt: string | null
  /**
   * The sign-in before this one.
   *
   * This is the figure a person can act on. `lastLoginAt` during a session is the session they
   * are looking at, which tells them nothing about whether anyone else has used the account.
   */
  previousLoginAt: string | null
  lastLoginIp: string | null
  passwordUpdatedAt: string | null
  activeSessions: number
  sessionIdleExpiresAt: string
  sessionAbsoluteExpiresAt: string
}

/** Why a sign-in was refused. */
export interface LoginFailure {
  status: 'InvalidCredentials' | 'AccountLocked' | 'AccountDisabled'
  message: string
  lockedUntil: string | null
}

/** A device this account has signed in from. */
export interface SessionInfo {
  id: string
  createdAt: string
  lastSeenAt: string
  idleExpiresAt: string
  absoluteExpiresAt: string
  revokedAt: string | null
  revokedReason: string | null
  ip: string | null
  userAgent: string | null
  isCurrent: boolean
}

/** Query key for the current user. Exported so anything can invalidate it. */
export const CURRENT_USER_KEY = ['auth', 'me'] as const

/**
 * The current user, or null when nobody is signed in.
 *
 * `retry: false` matters: a 401 is a definite answer, and retrying it three times would delay
 * the login screen by seconds for every visitor who is simply not signed in yet.
 */
export function useCurrentUser() {
  return useQuery({
    queryKey: CURRENT_USER_KEY,
    queryFn: ({ signal }) => api.get<CurrentUser>('/api/v1/auth/me', undefined, signal),
    retry: false,
    // The permission set is read fresh from the database on every API request anyway, so this
    // copy only drives what the UI offers. A minute is short enough that a role change becomes
    // visible without a reload, and long enough not to refetch on every focus change.
    staleTime: 60_000,
  })
}

export function useLogin() {
  const client = useQueryClient()

  return useMutation({
    mutationFn: (credentials: { username: string; password: string }) =>
      api.post<CurrentUser>('/api/v1/auth/login', credentials),
    onSuccess: (user) => {
      // Seeded rather than invalidated: the login response already IS the current user, and
      // refetching it would add a round trip before the dashboard can render.
      client.setQueryData(CURRENT_USER_KEY, user)
    },
  })
}

export function useLogout() {
  const client = useQueryClient()

  return useMutation({
    mutationFn: () => api.post<void>('/api/v1/auth/logout', {}),
    onSettled: () => {
      // Cleared on settle, not on success. If the sign-out request fails the session may still
      // be gone server-side, and leaving a stale cached user behind would show a signed-in shell
      // over an API that answers 401 to everything.
      client.clear()
    },
  })
}

export function useUpdateProfile() {
  const client = useQueryClient()

  return useMutation({
    mutationFn: (update: {
      displayName: string
      email: string | null
      jobTitle: string | null
      phone: string | null
    }) => api.put<CurrentUser>('/api/v1/auth/me', update),
    onSuccess: (user) => client.setQueryData(CURRENT_USER_KEY, user),
  })
}

export function useChangePassword() {
  const client = useQueryClient()

  return useMutation({
    mutationFn: (passwords: { currentPassword: string; newPassword: string }) =>
      api.post<void>('/api/v1/auth/me/password', passwords),
    onSuccess: () => {
      // The forced-password-change flag has just cleared, and every other session was revoked.
      client.invalidateQueries({ queryKey: CURRENT_USER_KEY })
      client.invalidateQueries({ queryKey: ['auth', 'sessions'] })
    },
  })
}

export function useMySessions() {
  return useQuery({
    queryKey: ['auth', 'sessions'],
    queryFn: ({ signal }) => api.get<SessionInfo[]>('/api/v1/auth/me/sessions', undefined, signal),
    staleTime: 30_000,
  })
}

export function useRevokeMySession() {
  const client = useQueryClient()

  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`/api/v1/auth/me/sessions/${id}`),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ['auth', 'sessions'] })
      client.invalidateQueries({ queryKey: CURRENT_USER_KEY })
    },
  })
}
