import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

/*
  Route-level code splitting. The dashboard pulls in ECharts; the lookup page
  does not, and should not have to wait for it to parse.
*/
const routes: RouteRecordRaw[] = [
  {
    path: '/',
    name: 'dashboard',
    component: () => import('@/features/dashboard/DashboardPage.vue'),
    meta: { title: 'Device population' },
  },
  {
    path: '/imports',
    name: 'imports',
    component: () => import('@/features/imports/ImportCenterPage.vue'),
    meta: { title: 'Import Center' },
  },
  {
    path: '/imports/:jobId(\d+)',
    name: 'import-detail',
    component: () => import('@/features/imports/ImportDetailPage.vue'),
    meta: { title: 'Import' },
  },
  {
    path: '/lookup',
    name: 'lookup',
    component: () => import('@/features/lookup/LookupPage.vue'),
    meta: { title: 'Subscriber lookup' },
  },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

router.afterEach((to) => {
  document.title = to.meta['title'] ? `${to.meta['title']} · Device Intelligence` : 'Device Intelligence'
})
