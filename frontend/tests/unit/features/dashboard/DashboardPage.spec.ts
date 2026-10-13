import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, type PropType } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import { DASHBOARDS_KEY, type DashboardDetail, type DashboardList } from '@/api/dashboards'
import DashboardPage from '@/features/dashboard/DashboardPage.vue'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

/*
  The page's own decisions, on the real data hooks against a stubbed API: which revision a save
  names, where Duplicate stops, what the browser tab is called. The grid and the widgets are
  stand-ins - GridStack needs layout, and the widgets' own rules have their own specs.
*/
vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...actual, useAuth: () => ({ can: () => true }) }
})

const Grid = defineComponent({
  name: 'DashboardGrid',
  props: { widgets: { type: Array as PropType<WidgetInstance[]>, required: true }, editable: Boolean },
  emits: ['commit', 'columns'],
  setup(props, { slots }) {
    return () => h('div', props.widgets.map((widget) => slots.item?.({ widget, columns: 12 })))
  },
})

const Host = defineComponent({
  name: 'WidgetHost',
  props: { widget: { type: Object as PropType<WidgetInstance>, required: true }, full: Boolean },
  emits: ['action'],
  setup(props) {
    return () => h('div', { 'data-widget-id': props.widget.id })
  },
})

const definition = JSON.parse(JSON.stringify(devicePopulation.definition))
const access = { level: 'owner', canEdit: true, canShare: true, canDelete: true, canDuplicate: true, canExportData: true } as const

function detail(patch: Partial<DashboardDetail> = {}): DashboardDetail {
  return {
    id: 7,
    name: 'Operations',
    description: 'What the night shift watches',
    owner: { id: 1, displayName: 'Me' },
    definition,
    schemaVersion: 1,
    revision: 3,
    sourceTemplate: null,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    updatedBy: null,
    access,
    isFavorite: false,
    isDefault: false,
    grantCount: 0,
    ...patch,
  }
}

function list(maxWidgets: number): DashboardList {
  return {
    mine: [],
    sharedWithMe: [],
    others: null,
    default: { dashboardId: null, templateKey: null },
    limits: {
      maxWidgets,
      maxNameLength: 120,
      maxDescriptionLength: 1000,
      maxTitleLength: 80,
      maxSubtitleLength: 120,
      maxWidgetConfigBytes: 8192,
      maxDefinitionBytes: 262144,
      maxGrants: 100,
    },
  }
}

let queryClient: QueryClient
let wrapper: VueWrapper | null = null

async function open(maxWidgets = 24) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: DashboardPage },
      { path: '/dashboards/:id(\\d+)', name: 'dashboard-view', component: DashboardPage },
      { path: '/dashboards/templates/:key', name: 'dashboard-template', component: DashboardPage },
    ],
  })
  // As the app's own router does: every navigation resets the tab's title to the route's.
  router.afterEach(() => {
    document.title = 'QuadraSense'
  })
  vi.spyOn(api, 'get').mockImplementation(async (path: string) => {
    if (path === '/api/v1/dashboards') return list(maxWidgets) as never
    if (path === '/api/v1/dashboards/7') return detail() as never
    return [] as never
  })
  await router.push('/dashboards/7')
  await router.isReady()
  wrapper = mount(DashboardPage, {
    global: {
      plugins: [router, [VueQueryPlugin, { queryClient }]],
      stubs: {
        DashboardGrid: Grid,
        WidgetHost: Host,
        LensBar: true,
        AddWidgetDialog: true,
        ShareDialog: true,
        ExportDialog: true,
        DashboardSettingsDialog: true,
        ConflictDialog: true,
        DeleteDialog: true,
        ConfirmDialog: true,
        NameDialog: true,
      },
    },
  })
  await flushPromises()
  return { wrapper, router }
}

function button(w: VueWrapper, text: string) {
  const found = w.findAll('button').find((b) => b.text() === text)
  if (!found) throw new Error(`no "${text}" button`)
  return found
}

function hosts(w: VueWrapper) {
  return w.findAllComponents(Host)
}

describe('the dashboard page', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
  })

  it('saves against the revision the edit started from, even after a refetch moved the cache on', async () => {
    const { wrapper } = await open()
    await button(wrapper, 'Edit').trigger('click')
    await hosts(wrapper)[0]!.vm.$emit('action', 'remove')

    // Somebody else saved; a reconnect refetched the detail while this person was still editing.
    queryClient.setQueryData([...DASHBOARDS_KEY, 'detail', 7], detail({ revision: 4, updatedBy: 'someone else' }))
    await flushPromises()

    const put = vi.spyOn(api, 'put').mockImplementation(async () => detail({ revision: 5 }) as never)
    await button(wrapper, 'Save').trigger('click')
    await flushPromises()

    expect(put).toHaveBeenCalledTimes(1)
    expect((put.mock.calls[0]![1] as { expectedRevision: number }).expectedRevision).toBe(3)
  })

  it('does not duplicate past the most widgets a dashboard may hold', async () => {
    const { wrapper } = await open(definition.widgets.length)
    await button(wrapper, 'Edit').trigger('click')
    expect(hosts(wrapper)[0]!.props('full')).toBe(true)

    await hosts(wrapper)[0]!.vm.$emit('action', 'duplicate')
    await flushPromises()
    expect(hosts(wrapper)).toHaveLength(definition.widgets.length)
  })

  it('duplicates below the limit', async () => {
    const { wrapper } = await open(definition.widgets.length + 1)
    await button(wrapper, 'Edit').trigger('click')
    expect(hosts(wrapper)[0]!.props('full')).toBe(false)

    await hosts(wrapper)[0]!.vm.$emit('action', 'duplicate')
    await flushPromises()
    expect(hosts(wrapper)).toHaveLength(definition.widgets.length + 1)
  })
})
