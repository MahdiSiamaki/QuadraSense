import { Permission, type PermissionCode } from '@/features/auth/useAuth'

/**
 * Everything that configures the system rather than uses it, in one place.
 *
 * These pages used to sit in the main bar beside the dashboard and the lookups - Users, Audit,
 * Image review - so the bar every analyst reads all day carried the destinations an administrator
 * visits now and then. They are grouped here by what they govern, and the bar keeps the work.
 *
 * One list, read by both the side menu and the router's landing redirect, so the two can never
 * disagree about what a section is called, where it lives or who may open it.
 */
export interface SettingsSection {
  to: string
  label: string
  description: string
  group: 'Personal' | 'Access' | 'Oversight' | 'Catalogue'
  /** Any one of these opens it; none listed means everyone. */
  anyOf: readonly PermissionCode[]
}

export const SETTINGS_SECTIONS: readonly SettingsSection[] = [
  {
    to: '/settings/appearance',
    label: 'Appearance',
    description: 'Light, dark, or follow the system.',
    group: 'Personal',
    anyOf: [],
  },
  {
    to: '/settings/users',
    label: 'Users',
    description: 'Who can sign in, and the roles they hold.',
    group: 'Access',
    anyOf: [Permission.UserView],
  },
  {
    to: '/settings/roles',
    label: 'Roles',
    description: 'Named sets of permissions, and the matrix of all of them.',
    group: 'Access',
    anyOf: [Permission.RoleView],
  },
  {
    to: '/settings/audit',
    label: 'Audit log',
    description: 'Sign-ins, changes, imports and every refused request.',
    group: 'Oversight',
    anyOf: [Permission.AuditView],
  },
  {
    to: '/settings/device-images',
    label: 'Device images',
    description: 'Proposed product images, live only once approved.',
    group: 'Catalogue',
    anyOf: [Permission.DeviceImageManage],
  },
]

export function sectionsFor(permissions: readonly string[]): SettingsSection[] {
  return SETTINGS_SECTIONS.filter(
    (s) => s.anyOf.length === 0 || s.anyOf.some((p) => permissions.includes(p)),
  )
}
