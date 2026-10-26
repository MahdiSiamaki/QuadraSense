// TEMPORARY - not committed. Proves the additive design-system props leave every existing caller's
// output identical: the HEAD version and the new version are mounted with the same props and the
// ECharts option (formatters evaluated on samples) or the rendered HTML compared.
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick, type Component } from 'vue'

const captured: unknown[] = []
vi.mock('echarts/core', () => ({
  use: () => {},
  init: () => ({
    setOption: (o: unknown) => captured.push(o),
    on: () => {},
    resize: () => {},
    dispose: () => {},
  }),
}))

if (!('ResizeObserver' in globalThis)) {
  ;(globalThis as Record<string, unknown>)['ResizeObserver'] = class {
    observe() {}
    disconnect() {}
  }
}

import NewChange from '@/design-system/ChangeTimeSeries.vue'
import OldChange from './old/ChangeTimeSeries.vue'
import NewChurn from '@/design-system/ChurnTimeSeries.vue'
import OldChurn from './old/ChurnTimeSeries.vue'
import NewBar from '@/design-system/BarChart.vue'
import OldBar from './old/BarChart.vue'
import NewTable from '@/design-system/DimensionTable.vue'
import OldTable from './old/DimensionTable.vue'
import NewKpi from '@/design-system/KpiCard.vue'
import OldKpi from './old/KpiCard.vue'

const SAMPLES: unknown[] = [
  -1234567,
  1234567,
  0.5,
  { value: 1234567, dataIndex: 0, name: 'Samsung' },
  { value: 7, dataIndex: 1, name: '(unknown device)' },
  [{ dataIndex: 0 }],
  [{ dataIndex: 1 }],
  [{ dataIndex: 2 }],
  [{ dataIndex: 3 }],
]

function normalise(value: unknown): unknown {
  if (typeof value === 'function') {
    return SAMPLES.map((s) => {
      try {
        return String((value as (a: unknown) => unknown)(s))
      } catch (e) {
        return `throws ${(e as Error).message}`
      }
    })
  }
  if (Array.isArray(value)) return value.map(normalise)
  if (value && typeof value === 'object' && !(value instanceof Map)) {
    const out: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(value)) out[k] = normalise(v)
    return out
  }
  return value
}

async function option(component: Component, props: Record<string, unknown>) {
  captured.length = 0
  const w = mount(component, { props })
  await nextTick()
  const o = captured[captured.length - 1]
  w.unmount()
  return normalise(o)
}

const changes = [
  { date: '2026-03-01', added: 10, removed: 4, net: 6, cumulative: 1000, unknownDeviceRows: 0 },
  { date: '2026-03-02', added: 7, removed: 9, net: -2, cumulative: 998, unknownDeviceRows: 0 },
  { date: '2026-03-04', added: 3, removed: 1, net: 2, cumulative: 1000, unknownDeviceRows: 0 },
]
const churn = [
  { date: '2026-03-01', simChanges: 3, deviceChanges: 40 },
  { date: '2026-03-03', simChanges: 5, deviceChanges: 41 },
]
const flagged = new Map([['2026-03-02', 'IMEIs shifted by one digit']])
const rows = [
  { key: 'Samsung', count: 1234567, percent: 41.234 },
  { key: '(unknown device)', count: 7, percent: 0.001 },
  { key: 'Apple', count: 900, percent: 12 },
]

describe('additive props keep every existing caller identical', () => {
  it('ChangeTimeSeries, as the dashboard page called it', async () => {
    const props = { data: changes, flagged }
    expect(await option(NewChange, props)).toEqual(await option(OldChange, props))
  })

  it('ChangeTimeSeries with no flagged days', async () => {
    const props = { data: changes }
    expect(await option(NewChange, props)).toEqual(await option(OldChange, props))
  })

  it('ChurnTimeSeries, as the dashboard page called it', async () => {
    const props = { data: churn, flagged }
    expect(await option(NewChurn, props)).toEqual(await option(OldChurn, props))
  })

  it('BarChart, as Top vendors and Top models called it', async () => {
    for (const props of [{ data: rows, unit: 'bindings' }, { data: rows, unit: 'handsets', horizontal: false }, { data: rows }]) {
      expect(await option(NewBar, props)).toEqual(await option(OldBar, props))
    }
  })

  it('DimensionTable renders the same HTML', () => {
    for (const props of [{ rows, header: 'Device type', unit: 'bindings' }, { rows, header: 'OS' }]) {
      expect(mount(NewTable, { props }).html().replace(/\r/g, '')).toBe(mount(OldTable, { props }).html().replace(/\r/g, ''))
    }
  })

  it('KpiCard renders the same HTML', () => {
    const cases = [
      { label: 'Active bindings', value: 115151591, identifier: 'MSISDN + IMSI + IMEI', definition: 'Rows.' },
      { label: 'TAC coverage', value: 93.26, percent: true, identifier: 'TAC', qualifier: 'of bindings' },
      { label: 'TAC coverage', value: 0.02, percent: true },
      { label: 'Unknown device', value: 8800000, tone: 'warning' as const, qualifier: '7.6% of bindings' },
      { label: 'Empty', value: undefined },
    ]
    for (const props of cases) {
      const a = mount(NewKpi, { props }).html().replace(/v-\d+/g, 'id')
      const b = mount(OldKpi, { props }).html().replace(/v-\d+/g, 'id')
      expect(a).toBe(b)
    }
  })
})
