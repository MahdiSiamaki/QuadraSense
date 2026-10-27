import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { ref } from 'vue'

/*
  The planning page shows the server's classes and says what the data cannot answer. Nothing here is
  classed by the page: the API module is mocked and the page is checked for what it puts on screen,
  and for what it holds back from someone who may not look up numbers.
*/
const held = new Set<string>()

vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...original, useAuth: () => ({ can: (p: string) => held.has(p) }) }
})

vi.mock('vue-router', () => ({
  useRoute: () => ({ query: { tab: 'sunset' } }),
  useRouter: () => ({ replace: vi.fn() }),
  RouterLink: { template: '<a><slot /></a>' },
}))

const run = { runId: 1790000000000, asOfSequence: 246, asOfDate: '2026-10-10', publishedAt: '2026-10-27T05:00:00Z', hasNumbers: true }

const sunset = {
  run,
  filtered: false,
  totalNumbers: 1000,
  classes: [
    { class: 'ready-lte', label: 'Ready: LTE or 5G', numbers: 700, bindings: 900, handsets: 800, sharePercent: 70 },
    { class: 'ready-lpwan', label: 'Ready: LTE-M / NB-IoT only', numbers: 10, bindings: 10, handsets: 10, sharePercent: 1 },
    { class: 'exposed', label: 'Exposed: 2G/3G only', numbers: 190, bindings: 200, handsets: 195, sharePercent: 19 },
    { class: 'not-assessable', label: 'Not assessable', numbers: 100, bindings: 120, handsets: 90, sharePercent: 10 },
  ],
  notAssessable: [{ class: 'unknown-device', label: 'An unknown device (000000 or malformed IMEI)', numbers: 100, bindings: 120, handsets: 0, sharePercent: 10 }],
  exposedGsmOnly: 40,
  exposedIot: 12,
  paired: 3,
  trend: [],
  modelTrend: [],
  noData: [{ topic: 'VoLTE', reason: 'The GSMA catalogue has no VoLTE field.' }],
}

const query = <T>(value: T) => ({ data: ref(value), isPending: ref(false), isError: ref(false), error: ref(null), refetch: vi.fn() })

vi.mock('@/api/planning', () => ({
  usePlanningStatus: () =>
    query({ deployed: true, runs: [run], running: null, stale: null, catalogue: { tacVersionId: 1595, parserVersion: 1, models: 271830, unparsedModels: 709, volteMentions: 2 } }),
  useSunset: () => query(sunset),
  useEsim: () => query(null),
  useIot: () => query(null),
}))

const { default: PlanningPage } = await import('@/features/planning/PlanningPage.vue')

function render() {
  return mount(PlanningPage, {
    global: {
      stubs: { PlanningBreakdown: true, PlanningNumbers: true, PlanningTrendChart: true },
    },
  })
}

beforeEach(() => held.clear())

describe('the planning page', () => {
  it('shows each class as a share of the stated denominator', async () => {
    held.add('planning.view')
    const page = render()
    await flushPromises()

    const text = page.text()
    expect(text).toContain('Exposed: 2G/3G only')
    expect(text).toContain('190')
    expect(text).toContain('19.0% of numbers')
    expect(text).toContain('GSM (2G) only')
  })

  it('says VoLTE has no data rather than showing a number for it', async () => {
    held.add('planning.view')
    const text = render().text()

    expect(text).toContain('VoLTE: no data')
  })

  it('holds the numbers back from someone who may not look them up', async () => {
    held.add('planning.view')
    const without = render()
    expect(without.findComponent({ name: 'PlanningNumbers' }).exists()).toBe(false)

    held.add('lookup.subscriber')
    const withLookup = render()
    expect(withLookup.findComponent({ name: 'PlanningNumbers' }).exists()).toBe(true)
  })
})
