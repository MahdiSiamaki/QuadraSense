import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import ConfigForm from '@/features/dashboard/components/ConfigForm.vue'
import DashboardSwitcher from '@/features/dashboard/components/DashboardSwitcher.vue'
import LensBar from '@/features/dashboard/components/LensBar.vue'
import WidgetFrame from '@/features/dashboard/components/WidgetFrame.vue'
import { getWidget } from '@/features/dashboard/registry'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

/*
  The dashboard core's defects the branch review confirmed, each pinned so it cannot come back.
  Signed in with every permission: the form offers only the values a person may use.
*/
vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...actual, useAuth: () => ({ can: () => true }) }
})

function instance(type: string, config = defaultConfig(type)): WidgetInstance {
  return {
    id: 'w_test0001',
    type,
    configVersion: CONTRACT.types[type]!.configVersion,
    title: null,
    subtitle: null,
    config,
    overrides: {},
    layout: { x: 0, y: 0, w: 6, h: 6 },
  } as WidgetInstance
}

describe('the dashboard switcher', () => {
  const props = { list: { mine: [], sharedWithMe: [], others: null, default: null, limits: {} } as never, currentId: null, currentTemplate: null, label: 'Device population' }

  it("offers an administrator everybody else's dashboards until they are loaded", async () => {
    const wrapper = mount(DashboardSwitcher, { props: { ...props, others: null, canManage: true }, attachTo: document.body })
    await wrapper.get('button[aria-expanded]').trigger('click')

    const load = wrapper.findAll('button').find((b) => b.text().includes('Show them (administrator)'))
    expect(load, 'the load button').toBeTruthy()
    await load!.trigger('click')
    expect(wrapper.emitted('loadOthers')).toHaveLength(1)
    wrapper.unmount()
  })

  it('keeps the page heading a heading: the h1 holds the button, never the reverse', () => {
    const wrapper = mount(DashboardSwitcher, { props: { ...props, others: null, canManage: false } })
    const h1 = wrapper.get('h1')
    expect(h1.text()).toContain('Device population')
    expect(h1.element.closest('button')).toBeNull()
    expect(h1.find('button[aria-expanded]').exists()).toBe(true)
  })
})

describe('the widget frame', () => {
  it("draws a bare card's menu in view mode with no badges: a KPI keeps Download CSV", () => {
    const wrapper = mount(WidgetFrame, {
      props: { title: 'Active bindings', chrome: 'bare', editing: false, badges: [], menu: [{ key: 'export:csv', label: 'Download CSV' }] },
    })
    expect(wrapper.find('[aria-label="Actions for Active bindings"]').exists()).toBe(true)
  })
})

describe('the configure form', () => {
  beforeEach(() => {
    vi.spyOn(api, 'get').mockImplementation(() => new Promise(() => {}))
  })
  const global = { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] as never }

  it("draws a field placed in the general section: a quick lookup's target", () => {
    const wrapper = mount(ConfigForm, {
      props: {
        definition: getWidget('tools.quickLookup')!,
        config: defaultConfig('tools.quickLookup'),
        title: '',
        subtitle: '',
        overrides: {},
        dashboardView: {},
        errors: [],
        instance: instance('tools.quickLookup'),
      },
      global,
    })
    expect(wrapper.text()).toContain('Opens')
    expect(wrapper.text()).toContain('IMSI search')
  })

  it('lets a number box be emptied while a new number is typed, instead of writing the default back', async () => {
    const config = { ...defaultConfig('device.top'), limit: 5 }
    const wrapper = mount(ConfigForm, {
      props: {
        definition: getWidget('device.top')!,
        config,
        title: '',
        subtitle: '',
        overrides: {},
        dashboardView: {},
        errors: [],
        instance: instance('device.top', config),
      },
      global,
    })
    const input = wrapper.get('input[type="number"]')
    ;(input.element as HTMLInputElement).value = ''
    await input.trigger('input')

    const emitted = wrapper.emitted('update:config')!.at(-1)![0] as Record<string, unknown>
    expect(emitted.limit).toBe('')
  })
})

describe('the lens bar', () => {
  it('says what the unknown-devices button will do, without a pressed state that contradicts it', () => {
    const wrapper = mount(LensBar, {
      props: { widgets: [instance('device.top')], view: { includeUnknownDevice: false }, defaults: {} },
    })
    const toggle = wrapper.findAll('button').find((b) => b.text().includes('unknown devices'))!
    expect(toggle.text()).toBe('Show unknown devices')
    expect(toggle.attributes('aria-pressed')).toBeUndefined()
  })
})
