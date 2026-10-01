import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { queryClient } from '@/lib/queryClient'
import { CURRENT_USER_KEY, forgetSession, type CurrentUser } from '@/api/auth'
import { api, ApiError, setUnauthenticatedHandler } from '@/api/client'
import { Permission } from '@/features/auth/useAuth'
import { sectionsFor } from '@/features/settings/sections'

/*
  Route-level code splitting. The dashboard pulls in ECharts; the lookup page
  does not, and should not have to wait for it to parse.
*/
/** An administrator lands on what they administer; everyone else on Appearance. */
function firstSettingsSection(permissions: readonly string[]): string {
  const sections = sectionsFor(permissions)
  return (sections.find((s) => s.group !== 'Personal') ?? sections[0])?.to ?? '/settings/appearance'
}

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/features/auth/LoginPage.vue'),
    meta: { title: 'Sign in', public: true, chrome: false },
  },
  {
    path: '/change-password',
    name: 'change-password',
    component: () => import('@/features/auth/ForcedPasswordChangePage.vue'),
    // Reachable while a password change is pending, which is the one state where every other
    // route is refused by the API.
    meta: { title: 'Change password', chrome: false, allowDuringPasswordChange: true },
  },
  {
    path: '/',
    name: 'dashboard',
    component: () => import('@/features/dashboard/DashboardPage.vue'),
    meta: { title: 'Device population', permission: Permission.DashboardView },
  },
  {
    path: '/imports',
    name: 'imports',
    component: () => import('@/features/imports/ImportCenterPage.vue'),
    meta: { title: 'Import Center', permission: Permission.ImportView },
  },
  {
    path: '/imports/:jobId(\\d+)',
    name: 'import-detail',
    component: () => import('@/features/imports/ImportDetailPage.vue'),
    meta: { title: 'Import', permission: Permission.ImportView },
  },
  {
    path: '/devices',
    name: 'devices',
    component: () => import('@/features/devices/DeviceListPage.vue'),
    meta: { title: 'Devices', permission: Permission.DeviceView },
  },
  {
    // Eight digits, matching every TAC in the GSMA export. The constraint is real validation as
    // well as routing: a malformed code never reaches a query.
    path: '/devices/:tac(\\d{8})',
    name: 'device-detail',
    component: () => import('@/features/devices/DeviceDetailPage.vue'),
    meta: { title: 'Device', permission: Permission.DeviceView },
  },
  {
    // No identifiers in this route, by design: queries are POSTed, and drill-down is a panel.
    path: '/explorer',
    name: 'explorer',
    component: () => import('@/features/explorer/ExplorerPage.vue'),
    meta: { title: 'Explorer', permission: Permission.ExplorerQuery },
  },
  {
    path: '/lookup',
    name: 'lookup',
    component: () => import('@/features/lookup/LookupPage.vue'),
    meta: { title: 'Subscriber lookup', permission: Permission.LookupSubscriber },
  },
  {
    path: '/lookup/imsi',
    name: 'imsi-search',
    component: () => import('@/features/lookup/ImsiSearchPage.vue'),
    meta: { title: 'IMSI search', permission: Permission.LookupImsi },
  },
  {
    path: '/relationships',
    name: 'relationships',
    component: () => import('@/features/lookup/RelationshipExplorerPage.vue'),
    // The centre's own kind decides the real permission, checked server-side. This gate only
    // keeps the page out of the navigation for somebody who can look nothing up at all.
    meta: { title: 'Relationship explorer', permission: Permission.LookupSubscriber },
  },
  {
    path: '/profile',
    name: 'profile',
    component: () => import('@/features/profile/ProfilePage.vue'),
    meta: { title: 'Your profile' },
  },
  /*
    Settings: everything that configures the system rather than uses it, under one frame whose
    side menu lists the sections this user may open. Each page keeps its own permission; the
    frame itself needs none, because Appearance is for everybody.
  */
  {
    path: '/settings',
    component: () => import('@/features/settings/SettingsLayout.vue'),
    meta: { title: 'Settings' },
    children: [
      {
        // Lands on the first section this user can open, not on one that would refuse them.
        // beforeEnter rather than a redirect: it runs after the global guard, so the user is
        // known even on a hard refresh.
        path: '',
        name: 'settings',
        component: () => import('@/features/settings/AppearanceSettings.vue'),
        beforeEnter: () => {
          const user = queryClient.getQueryData<CurrentUser>(CURRENT_USER_KEY)
          return firstSettingsSection(user?.permissions ?? [])
        },
      },
      {
        path: 'appearance',
        name: 'settings-appearance',
        component: () => import('@/features/settings/AppearanceSettings.vue'),
        meta: { title: 'Appearance' },
      },
      {
        path: 'users',
        name: 'users',
        component: () => import('@/features/admin/users/UsersPage.vue'),
        meta: { title: 'Users', permission: Permission.UserView },
      },
      {
        path: 'users/:id(\\d+)',
        name: 'user-detail',
        component: () => import('@/features/admin/users/UserDetailPage.vue'),
        meta: { title: 'User', permission: Permission.UserView },
      },
      {
        path: 'roles',
        name: 'roles',
        component: () => import('@/features/admin/roles/RolesPage.vue'),
        meta: { title: 'Roles', permission: Permission.RoleView },
      },
      {
        path: 'roles/matrix',
        name: 'permission-matrix',
        component: () => import('@/features/admin/roles/PermissionMatrixPage.vue'),
        meta: { title: 'Permission matrix', permission: Permission.RoleView },
      },
      {
        path: 'roles/:id(\\d+)',
        name: 'role-detail',
        component: () => import('@/features/admin/roles/RoleDetailPage.vue'),
        meta: { title: 'Role', permission: Permission.RoleView },
      },
      {
        path: 'audit',
        name: 'audit',
        component: () => import('@/features/admin/audit/AuditLogPage.vue'),
        meta: { title: 'Audit log', permission: Permission.AuditView },
      },
      {
        path: 'device-images',
        name: 'device-image-review',
        component: () => import('@/features/devices/DeviceImageReviewPage.vue'),
        meta: { title: 'Device images', permission: Permission.DeviceImageManage },
      },
    ],
  },
  // Where these pages used to live. Bookmarks and links already sent keep working.
  { path: '/admin/users', redirect: (to) => ({ path: '/settings/users', query: to.query }) },
  { path: '/admin/users/:id(\\d+)', redirect: (to) => `/settings/users/${String(to.params['id'])}` },
  { path: '/admin/roles', redirect: '/settings/roles' },
  { path: '/admin/roles/matrix', redirect: '/settings/roles/matrix' },
  { path: '/admin/roles/:id(\\d+)', redirect: (to) => `/settings/roles/${String(to.params['id'])}` },
  { path: '/admin/audit', redirect: (to) => ({ path: '/settings/audit', query: to.query }) },
  { path: '/devices/image-review', redirect: (to) => ({ path: '/settings/device-images', query: to.query }) },
  {
    path: '/no-access',
    name: 'no-access',
    component: () => import('@/features/auth/NoAccessPage.vue'),
    meta: { title: 'No access' },
  },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

/**
 * Resolves the signed-in user once, and reuses the answer.
 *
 * `fetchQuery` rather than a bare request: the guard and the app share one cache, so the first
 * navigation populates what every component then reads, and a hard refresh does not fetch the
 * same thing twice.
 */
async function resolveUser(): Promise<CurrentUser | null> {
  const cached = queryClient.getQueryData<CurrentUser>(CURRENT_USER_KEY)
  if (cached) return cached

  try {
    return await queryClient.fetchQuery({
      queryKey: CURRENT_USER_KEY,
      queryFn: ({ signal }) => api.get<CurrentUser>('/api/v1/auth/me', undefined, signal),
      retry: false,
      staleTime: 60_000,
    })
  } catch (error) {
    // A 401 is the expected answer for a visitor who is not signed in, not a failure.
    if (error instanceof ApiError && error.status === 401) return null
    throw error
  }
}

/**
 * The navigation guard.
 *
 * <p>
 * This hides routes; it does not protect them. Every endpoint behind these pages checks the same
 * permission server-side, and an integration test asserts that none escapes it. The guard exists
 * so that a user is sent to the login page instead of watching a dashboard fail to load, and so
 * that a link to something they cannot reach explains itself rather than rendering an empty
 * shell full of 403s.
 * </p>
 */
router.beforeEach(async (to) => {
  if (to.meta['public']) return true

  const user = await resolveUser()

  if (!user) {
    // `next` so that an expired session returns the reader to the page they were on, rather than
    // silently dropping them on the dashboard.
    return { name: 'login', query: to.fullPath === '/' ? {} : { next: to.fullPath } }
  }

  // A forced password change outranks everything: the API refuses every other endpoint until it
  // is done, so any other route would render as a wall of access-denied messages.
  if (user.mustChangePassword && !to.meta['allowDuringPasswordChange']) {
    return { name: 'change-password' }
  }

  if (!user.mustChangePassword && to.name === 'change-password') {
    return { name: 'dashboard' }
  }

  const required = to.meta['permission'] as string | undefined
  if (required && !user.permissions.includes(required)) {
    // Not a silent redirect to the dashboard: a user sent somewhere they did not ask for, with
    // no explanation, reasonably concludes the link is broken.
    return { name: 'no-access', query: { permission: required, from: to.fullPath } }
  }

  return true
})

/**
 * Where to go when a request comes back 401 mid-session.
 *
 * Registered here rather than in the client so the fetch wrapper does not have to know the
 * router exists - the import would be circular, and a module that navigates from inside a
 * response handler is hard to follow when it misbehaves.
 */
setUnauthenticatedHandler(() => {
  if (router.currentRoute.value.meta['public']) return

  forgetSession(queryClient)
  const from = router.currentRoute.value.fullPath
  void router.replace({ name: 'login', query: from === '/' ? {} : { next: from } })
})

router.afterEach((to) => {
  document.title = to.meta['title']
    ? `${to.meta['title']} · QuadraSense`
    : 'QuadraSense'
})
