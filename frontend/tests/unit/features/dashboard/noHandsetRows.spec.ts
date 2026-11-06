import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { lookupMsisdn, type KpiSummary } from '@/api/dashboard'
import LensBar from '@/features/dashboard/components/LensBar.vue'
import { effectiveLens } from '@/features/dashboard/composables/lens'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'
import type { WidgetInstance } from '@/features/dashboard/types/definition'
import { activeRows, shareOfBindings, shareOfRows, unregisteredTacBindings } from '@/features/dashboard/widgets/overview/kpiMetrics'
import { lensFilters } from '@/features/dashboard/widgets/overview/shared/filters'
import EnrichmentWidget from '@/features/dashboard/widgets/quality/EnrichmentWidget.vue'
import SubscriberBindings from '@/features/lookup/SubscriberBindings.vue'

/*
  Rows with no handset stay stored and are left out of every count (ADR-020): what the dashboard asks
  for by default, and how a card accounts for rows it does not count.
*/
vi.mock('@/api/dashboard', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/dashboard')>()),
  lookupMsisdn: vi.fn(),
}))

vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...actual, useAuth: () => ({ can: () => true }) }
})

/* Delivery 247's own figures. */
const counted: KpiSummary = {
  activeBindings: 110_684_109,
  distinctSubscribers: 74_984_884,
  distinctSims: 76_314_146,
  distinctDevices: 82_066_166,
  unknownDeviceBindings: 5_398_819,
  malformedImeiBindings: 41_088,
  setAsideBindings: 5_439_907,
  tacMatchedBindings: 110_335_702,
  tacCoveragePercent: 99.685,
} as KpiSummary

const everyRow: KpiSummary = { ...counted, activeBindings: 116_124_016, setAsideBindings: 0 }

function instance(type: string): WidgetInstance {
  return {
    id: 'w_test0001',
    type,
    configVersion: CONTRACT.types[type]!.configVersion,
    title: null,
    subtitle: null,
    config: defaultConfig(type),
    overrides: {},
    layout: { x: 0, y: 0, w: 6, h: 6 },
  } as WidgetInstance
}

describe('rows with no handset, in the figures', () => {
  it('finds every active row whether they are counted or not', () => {
    expect(activeRows(counted)).toBe(116_124_016)
    expect(activeRows(everyRow)).toBe(116_124_016)
  })

  it('gets the same unregistered TACs either way', () => {
    expect(unregisteredTacBindings(counted)).toBe(348_407)
    expect(unregisteredTacBindings(everyRow)).toBe(348_407)
  })

  it('reads a report exported before the field existed', () => {
    const old = { ...everyRow } as Partial<KpiSummary>
    delete old.setAsideBindings
    expect(unregisteredTacBindings(old as KpiSummary)).toBe(348_407)
  })

  it('states their share of the rows, not of a population they are not in', () => {
    expect(shareOfRows(counted.unknownDeviceBindings, counted, 1)).toBe('4.6% of active rows')
    expect(shareOfRows(counted.unknownDeviceBindings, everyRow, 1)).toBe('4.6% of active rows')
  })

  it('keeps a share of what is counted as one', () => {
    expect(shareOfBindings(counted.tacMatchedBindings, counted, 1)).toBe('99.7% of bindings')
  })
})

describe('IMEIs no handset could carry', () => {
  /* The same delivery under the wider rule: a zero TAC, a shifted IMEI, a body GSMA never had. */
  const kpi: KpiSummary = {
    ...counted,
    activeBindings: 110_459_865,
    invalidImeiBindings: 224_244,
    setAsideBindings: 5_664_151,
  }

  function enrichment(display: string) {
    const widget = instance('quality.enrichment')
    return mount(EnrichmentWidget, {
      props: {
        instance: { ...widget, config: { ...widget.config, display } },
        lens: {},
        active: false,
        mode: 'export',
        snapshot: { id: widget.id, type: widget.type, status: 'ok', reason: null, data: { kpi }, asOf: null, notes: [] },
      } as never,
      global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] as never },
    })
  }

  it('are rows of their own, not TACs the catalogue lacks', () => {
    expect(activeRows(kpi)).toBe(116_124_016)
    expect(unregisteredTacBindings(kpi)).toBe(124_163)
  })

  it('are a line of the Enrichment breakdown, and the lines sum to every active row', () => {
    const text = enrichment('figures').text()
    expect(text).toContain('Invalid IMEI')
    expect(text).toContain('224,244')
    expect(text).toContain('The five figures sum to 116,124,016 active rows')
  })

  it('leave the shares of the breakdown shares of every active row', () => {
    const cells = enrichment('table').findAll('tbody tr').map((r) => r.findAll('td').at(-1)!.text())
    expect(cells).toEqual(['95.02%', '4.65%', '0.04%', '0.19%', '0.11%'])
  })
})

describe('looking a number up', () => {
  const none = { msisdn: '9120000000', wellFormed: true, count: 0, identifiers: 'Full' as const, bindings: [] }

  async function details(leftOut: number) {
    vi.mocked(lookupMsisdn).mockResolvedValue({ ...none, leftOut })
    const wrapper = mount(SubscriberBindings, { props: { msisdn: '9120000000' } })
    await flushPromises()
    return wrapper.text()
  }

  it('says a number with only rows with no handset holds none, not that it is unknown', async () => {
    expect(await details(1)).toContain('This number holds no handset.')
  })

  it('says a number nothing is known about is not found', async () => {
    expect(await details(0)).toContain('No bindings found for this number.')
  })
})

describe('rows with no handset, by default', () => {
  const spec = CONTRACT.types['device.top']!

  it('are left out of a widget that takes the lens', () => {
    expect(effectiveLens(spec, {}, {}, {}).includeUnknownDevice).toBe(false)
  })

  it('are left out of what a widget asks the API for', () => {
    expect(lensFilters({}).includeUnknownDevice).toBe(false)
  })

  it('come back when someone asks', () => {
    expect(effectiveLens(spec, {}, { includeUnknownDevice: true }, {}).includeUnknownDevice).toBe(true)
    expect(lensFilters({ includeUnknownDevice: true }).includeUnknownDevice).toBe(true)
  })

  it('offer the button that shows them', () => {
    const wrapper = mount(LensBar, { props: { widgets: [instance('device.top')], view: {}, defaults: {} } })
    const toggle = wrapper.findAll('button').find((b) => b.text().includes('unknown devices'))!
    expect(toggle.text()).toBe('Show unknown devices')
  })
})

describe('the Device population dashboard', () => {
  const definition = devicePopulation.definition

  it('opens with them left out', () => {
    expect(definition.defaults?.includeUnknownDevice).toBe(false)
  })

  it('has no figure for them in its key figures', () => {
    const row = definition.widgets.find((w) => w.type === 'metric.kpiSummary')!
    expect(row.config['metrics']).not.toContain('unknownDeviceBindings')
  })

  it('leaves the accounting of every row to Data operations', () => {
    expect(definition.widgets.map((w) => w.type)).not.toContain('quality.enrichment')
  })
})
