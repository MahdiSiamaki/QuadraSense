import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { queryClient } from '@/lib/queryClient'
import { CURRENT_USER_KEY, type CurrentUser } from '@/api/auth'
import { api, ApiError, setUnauthenticatedHandler } from '@/api/client'
import { Permission } from '@/features/auth/useAuth'

/*
  Route-level code splitting. The dashboard pulls in ECharts; the lookup page
  does not, and should not have to wait for it to parse.
*/
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
    path: '/lookup',
    name: 'lookup',
    component: () => import('@/features/lookup/LookupPage.vue'),
    meta: { title: 'Subscriber lookup', permission: Permission.LookupSubscriber },
  },
  {
    path: '/profile',
    name: 'profile',
    component: () => import('@/features/profile/ProfilePage.vue'),
    meta: { title: 'Your profile' },
  },
  {
    path: '/admin/users',
    name: 'users',
    component: () => import('@/features/admin/users/UsersPage.vue'),
    meta: { title: 'Users', permission: Permission.UserView },
  },
  {
    path: '/admin/users/:id(\\d+)',
    name: 'user-detail',
    component: () => import('@/features/admin/users/UserDetailPage.vue'),
    meta: { title: 'User', permission: Permission.UserView },
  },
  {
    path: '/admin/roles',
    name: 'roles',
    component: () => import('@/features/admin/roles/RolesPage.vue'),
    meta: { title: 'Roles', permission: Permission.RoleView },
  },
  {
    path: '/admin/roles/matrix',
    name: 'permission-matrix',
    component: () => import('@/features/admin/roles/PermissionMatrixPage.vue'),
    meta: { title: 'Permission matrix', permission: Permission.RoleView },
  },
  {
    path: '/admin/roles/:id(\\d+)',
    name: 'role-detail',
    component: () => import('@/features/admin/roles/RoleDetailPage.vue'),
    meta: { title: 'Role', permission: Permission.RoleView },
  },
  {
    path: '/admin/audit',
    name: 'audit',
    component: () => import('@/features/admin/audit/AuditLogPage.vue'),
    meta: { title: 'Audit log', permission: Permission.AuditView },
  },
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

  queryClient.clear()
  const from = router.currentRoute.value.fullPath
  void router.replace({ name: 'login', query: from === '/' ? {} : { next: from } })
})

router.afterEach((to) => {
  document.title = to.meta['title']
    ? `${to.meta['title']} · Device Intelligence`
    : 'Device Intelligence'
})
