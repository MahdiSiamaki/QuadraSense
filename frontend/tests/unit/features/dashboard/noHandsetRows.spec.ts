import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import type { KpiSummary } from '@/api/dashboard'
import LensBar from '@/features/dashboard/components/LensBar.vue'
import { effectiveLens } from '@/features/dashboard/composables/lens'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'
import type { WidgetInstance } from '@/features/dashboard/types/definition'
import { activeRows, shareOfBindings, shareOfRows, unregisteredTacBindings } from '@/features/dashboard/widgets/overview/kpiMetrics'
import { lensFilters } from '@/features/dashboard/widgets/overview/shared/filters'

/*
  Rows with no handset stay stored and are left out of every count (ADR-020): what the dashboard asks
  for by default, and how a card accounts for rows it does not count.
*/
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
