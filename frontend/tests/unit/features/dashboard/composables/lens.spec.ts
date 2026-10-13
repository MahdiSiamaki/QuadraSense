import { describe, expect, it } from 'vitest'
import {
  effectiveLens,
  isValidRange,
  resolveRange,
  sanitizeLens,
} from '@/features/dashboard/composables/lens'
import { CONTRACT, type WidgetTypeSpec } from '@/features/dashboard/registry/contract'

/*
  The same table backend/tests/Sqm.Application.Tests/Dashboards/DashboardLensAndAccessTests.cs pins
  for DashboardLensResolver. A widget must be computed with the same lens on the server (exports)
  as in the browser, case by case.
*/

const spec = (type: string): WidgetTypeSpec => CONTRACT.types[type]!

describe('effective lens', () => {
  it('L1: the URL wins over the defaults for a field the type supports', () => {
    expect(effectiveLens(spec('device.top'), { countBy: 'handsets', vendor: 'Samsung' }, { vendor: 'Apple' }, {})).toEqual({
      countBy: 'handsets',
      includeUnknownDevice: true,
      vendor: 'Apple',
    })
  })

  it('L2: an override wins over both', () => {
    expect(effectiveLens(spec('device.top'), { countBy: 'handsets' }, {}, { countBy: 'subscribers' }).countBy).toBe('subscribers')
  })

  it('L3: a network-wide series takes the range and nothing else', () => {
    expect(effectiveLens(spec('change.daily'), { range: 'last30', vendor: 'Samsung' }, {}, {})).toEqual({ range: 'last30' })
  })

  it('L4: a network-wide breakdown takes the count unit only', () => {
    expect(effectiveLens(spec('device.classMix'), {}, { countBy: 'subscribers', vendor: 'X' }, {})).toEqual({
      countBy: 'subscribers',
    })
  })

  it('L5: a KPI has its own unit but honours unknown devices', () => {
    const lens = effectiveLens(spec('metric.kpi'), {}, { countBy: 'handsets', includeUnknownDevice: false }, {})
    expect(lens.countBy).toBeUndefined()
    expect(lens.includeUnknownDevice).toBe(false)
  })

  it('L6: a unit the type cannot count by falls back to its first', () => {
    const bindingsOnly = { ...spec('device.top'), measures: ['bindings'] }
    expect(effectiveLens(bindingsOnly, {}, { countBy: 'handsets' }, {}).countBy).toBe('bindings')
  })
})

describe('ranges', () => {
  it('R1-R3: count back from the data, not from today', () => {
    expect(resolveRange('last30', '2026-10-06')).toEqual({ from: '2026-09-07', to: '2026-10-06' })
    expect(resolveRange('last30', null)).toEqual({ from: null, to: null })
    expect(resolveRange('2026-08-01..2026-08-31', null)).toEqual({ from: '2026-08-01', to: '2026-08-31' })
    expect(resolveRange('all', '2026-10-06')).toEqual({ from: null, to: null })
  })

  it.each([
    ['2026-02-30..2026-03-01', false],
    ['2026-09-01..2026-08-01', false],
    ['last7', true],
    ['last8', false],
    ['all', true],
    ['2026-08-01..2026-08-31', true],
  ])('R4: %s is %s', (range, valid) => {
    expect(isValidRange(range)).toBe(valid)
  })
})

describe('sanitising a hand-edited URL', () => {
  it('S1: degrades to no filter rather than failing', () => {
    const lens = sanitizeLens({
      countBy: 'devices' as never,
      vendor: '  Samsung ',
      tac: '1234567',
      deviceType: 'x'.repeat(121),
    })
    expect(lens).toEqual({ vendor: 'Samsung' })
  })
})
