import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import WidgetFrame from '@/features/dashboard/components/WidgetFrame.vue'
import WidgetHost from '@/features/dashboard/components/WidgetHost.vue'
import { getWidget } from '@/features/dashboard/registry'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

/*
  The host decides before anything mounts. A widget the person may not use sends no request at all:
  a 403 is audited, and a card that queries only to be refused teaches people to ignore errors.
*/
const granted = new Set<string>()
vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...actual, useAuth: () => ({ can: (p: string) => granted.has(p) }) }
})

function widget(type: string, patch: Partial<WidgetInstance> = {}): WidgetInstance {
  return {
    id: 'w_test0001',
    type,
    configVersion: CONTRACT.types[type]?.configVersion ?? 1,
    title: null,
    subtitle: null,
    config: CONTRACT.types[type] ? defaultConfig(type) : {},
    overrides: {},
    layout: { x: 0, y: 0, w: 6, h: 6 },
    ...patch,
  } as WidgetInstance
}

function host(instance: WidgetInstance) {
  return mount(WidgetHost, {
    props: { widget: instance, defaults: {}, urlLens: {}, mode: 'preview' },
    global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] },
  })
}

describe('the widget host', () => {
  let get: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    granted.clear()
    get = vi.spyOn(api, 'get').mockImplementation(() => new Promise(() => {}))
  })

  afterEach(() => vi.clearAllMocks())

  it('names a missing permission and mounts nothing, so nothing is requested', async () => {
    granted.add('dashboard.view')
    const wrapper = host(widget('risk.overview'))
    await flushPromises()

    expect(wrapper.text()).toContain('Needs risk.view')
    expect(wrapper.text()).toContain('Nothing was requested for it.')
    expect(wrapper.findComponent(getWidget('risk.overview')!.component).exists()).toBe(false)
    expect(get).not.toHaveBeenCalled()
  })

  it('follows the permission a config value needs: a SIM list needs lookup.imsi as well as risk.view', async () => {
    granted.add('dashboard.view').add('risk.view')
    const wrapper = host(widget('risk.list', { config: { ...defaultConfig('risk.list'), rule: 'HighDeviceCount30' } }))
    await flushPromises()

    expect(wrapper.text()).toContain('Needs lookup.imsi')
    expect(get).not.toHaveBeenCalled()
  })

  it('shows a fallback for a type this build does not know', () => {
    const wrapper = host(widget('traffic.volume'))
    expect(wrapper.text()).toContain('not available in this version')
  })

  it('shows a fallback for settings a newer version saved, rather than guessing at them', () => {
    granted.add('dashboard.view')
    const wrapper = host(widget('metric.kpi', { configVersion: 99 }))
    expect(wrapper.text()).toContain('saved by a newer version')
  })

  it('mounts the widget and lets it query once the person holds every permission', async () => {
    granted.add('dashboard.view').add('risk.view')
    const wrapper = host(widget('risk.overview'))
    await flushPromises()

    expect(wrapper.findComponent(getWidget('risk.overview')!.component).exists()).toBe(true)
    expect(get).toHaveBeenCalledWith('/api/v1/risk/status', undefined, expect.anything())
  })

  it('offers Duplicate in edit mode only while the dashboard has room, and says why not', () => {
    granted.add('dashboard.view')
    const menu = (full: boolean) =>
      mount(WidgetHost, {
        props: { widget: widget('metric.kpi'), defaults: {}, urlLens: {}, mode: 'edit', full },
        global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] },
      })
        .findComponent(WidgetFrame)
        .props('menu') as { key: string; disabled?: boolean; hint?: string }[]

    expect(menu(false).find((i) => i.key === 'duplicate')).toMatchObject({ disabled: false, hint: undefined })
    expect(menu(true).find((i) => i.key === 'duplicate')).toMatchObject({
      disabled: true,
      hint: 'This dashboard holds as many widgets as it may',
    })
  })
})
