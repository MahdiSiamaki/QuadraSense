import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, provide, ref } from 'vue'
import { QueryClient, VueQueryPlugin, useQuery } from '@tanstack/vue-query'
import { refreshDashboard } from '@/features/dashboard/composables/refresh'
import { useRefresh } from '@/features/dashboard/widgets/overview/shared/useRefresh'
import { DASHBOARD_CONTEXT, type DashboardContext } from '@/features/dashboard/types/widget'

/*
  Refresh sends each refreshable query once. A KPI card answers the refresh token itself
  (useRefresh), and the page also invalidates the query; Vue Query runs the invalidation on the next
  tick and, by default, cancels what it finds in flight - so the card's request was aborted and sent
  again, every time (2026-10-19, measured in the browser: /kpi twice, the first aborted at 8 ms).
*/
let wrapper: VueWrapper | null = null

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
})

/** A card on the real query and refresh hooks, whose request takes a moment, as a real one does. */
function mountCard(client: QueryClient) {
  const token = ref(0)
  const sent: AbortSignal[] = []

  const Card = defineComponent({
    setup() {
      const kpi = useQuery({
        queryKey: ['kpi', { deviceType: 'Smartphone' }],
        queryFn: ({ signal }) => {
          sent.push(signal)
          return new Promise<number>((resolve) => setTimeout(() => resolve(sent.length), 20))
        },
      })
      useRefresh([{ enabled: () => true, refetch: (options) => kpi.refetch(options) }])
      return () => h('div', String(kpi.data.value ?? ''))
    },
  })

  const Page = defineComponent({
    setup() {
      provide(DASHBOARD_CONTEXT, { refreshToken: token } as unknown as DashboardContext)
      return () => h(Card)
    },
  })

  wrapper = mount(Page, { global: { plugins: [[VueQueryPlugin, { queryClient: client }]] } })
  return { token, sent }
}

const settle = async () => {
  await flushPromises()
  await new Promise((resolve) => setTimeout(resolve, 60))
  await flushPromises()
}

describe('the dashboard Refresh', () => {
  it('sends a card that answers the token once, and aborts nothing', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { token, sent } = mountCard(client)
    await settle()
    expect(sent).toHaveLength(1)

    await refreshDashboard(client, token)
    await settle()

    expect(sent).toHaveLength(2)
    expect(sent.filter((s) => s.aborted)).toHaveLength(0)
    expect(token.value).toBe(1)
  })

  it('leaves what it may not repeat alone', async () => {
    const client = new QueryClient()
    client.setQueryData(['explorer', 'saved'], 'kept')
    client.setQueryData(['kpi', {}], 1)

    await refreshDashboard(client, ref(0))

    expect(client.getQueryState(['explorer', 'saved'])?.isInvalidated).toBe(false)
    expect(client.getQueryState(['kpi', {}])?.isInvalidated).toBe(true)
  })
})
