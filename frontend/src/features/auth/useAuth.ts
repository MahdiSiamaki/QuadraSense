import { computed } from 'vue'
import { useCurrentUser } from '@/api/auth'

/**
 * Permission codes, mirrored from Sqm.Application.Identity.Permissions.
 *
 * Duplicated deliberately, and the duplication is safe in a way the backend's is not: getting one
 * wrong here hides a button, whereas getting one wrong there would open a route. The server
 * checks every request regardless of what this file says.
 */
export const Permission = {
  DashboardView: 'dashboard.view',
  LookupSubscriber: 'lookup.subscriber',
  DataExport: 'data.export',
  LookupImsi: 'lookup.imsi',
  IdentifierReveal: 'identifier.reveal',
  DeviceView: 'device.view',
  LookupImei: 'lookup.imei',
  DeviceIdentifiers: 'device.identifiers',
  DeviceImageManage: 'device.image.manage',
  ImportView: 'import.view',
  ImportUploadSqm: 'import.upload.sqm',
  ImportUploadTac: 'import.upload.tac',
  ImportReprocess: 'import.reprocess',
  ImportCancel: 'import.cancel',
  ImportDelete: 'import.delete',
  TacActivate: 'tac.activate',
  TacRollback: 'tac.rollback',
  UserView: 'user.view',
  UserManage: 'user.manage',
  RoleView: 'role.view',
  RoleManage: 'role.manage',
  AuditView: 'audit.view',
  SystemAdmin: 'system.admin',
} as const

export type PermissionCode = (typeof Permission)[keyof typeof Permission]

/**
 * The signed-in user and what they may do.
 *
 * <p>
 * Hiding a control the user cannot use is a usability feature, not a security control - the
 * server enforces every one of these independently, and an integration test asserts that no
 * endpoint escapes it. The reason to hide anyway is that a screen full of buttons that return
 * "not permitted" teaches people to ignore error messages.
 * </p>
 */
export function useAuth() {
  const query = useCurrentUser()

  const user = computed(() => query.data.value ?? null)
  const permissions = computed(() => new Set(user.value?.permissions ?? []))

  /** Whether the user holds a permission. False while the session is still loading. */
  function can(permission: PermissionCode | string): boolean {
    return permissions.value.has(permission)
  }

  /** Whether the user holds at least one of these. */
  function canAny(...codes: Array<PermissionCode | string>): boolean {
    return codes.some((code) => permissions.value.has(code))
  }

  return {
    query,
    user,
    /** Resolved and signed in. */
    isAuthenticated: computed(() => user.value !== null),
    /** Still finding out. The app must not decide anything yet. */
    isResolving: computed(() => query.isPending.value),
    /**
     * The API will refuse everything except the password change until this clears.
     */
    mustChangePassword: computed(() => user.value?.mustChangePassword === true),
    /** True for anyone who can reach any part of the administration section. */
    isAdministrator: computed(
      () =>
        permissions.value.has(Permission.UserView) ||
        permissions.value.has(Permission.RoleView) ||
        permissions.value.has(Permission.AuditView),
    ),
    can,
    canAny,
  }
}
