import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import type { DashboardDetail, DashboardGrants } from '@/api/dashboards'
import ShareDialog from '@/features/dashboard/components/ShareDialog.vue'

/*
  A grant's level on screen is the grant's level on the server: a change the server refused must not
  leave the list showing what was asked for.
*/
const dashboard = { id: 7, name: 'Operations' } as DashboardDetail

const grants: DashboardGrants = {
  grants: [
    {
      id: 11,
      subjectType: 'role',
      subjectId: 3,
      subjectName: 'Analysts',
      subjectDetail: null,
      level: 'viewer',
      grantedAt: '2026-10-01T08:00:00Z',
      grantedBy: 'Me',
    },
  ],
  maxGrants: 50,
}

let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
})

describe('the share dialog', () => {
  it("shows a grant's real level again when a change to it is refused", async () => {
    vi.spyOn(api, 'get').mockImplementation(async (path: string) =>
      (path === '/api/v1/dashboards/7/grants' ? grants : { users: [], roles: [] }) as never,
    )
    const put = vi.spyOn(api, 'put').mockRejectedValue(new Error('Only the owner can widen access.'))
    wrapper = mount(ShareDialog, {
      props: { open: false, dashboard },
      global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
      attachTo: document.body,
    })
    await wrapper.setProps({ open: true })
    await flushPromises()

    const select = wrapper.get<HTMLSelectElement>('#grant-level-11')
    await select.setValue('editor')
    await flushPromises()

    expect(put).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Only the owner can widen access.')
    expect(select.element.value).toBe('viewer')
  })
})
