import { describe, expect, it } from 'vitest'
import type { DailyChange, DailyChurn } from '@/api/dashboard'
import { comparePeriods, periodFigure, previousWindow } from '@/features/dashboard/widgets/change/periodComparison'
import type { PeriodComparisonConfig } from '@/features/dashboard/widgets/change/config'

function day(date: string, added: number, removed: number, cumulative: number): DailyChange {
  return { date, added, removed, net: added - removed, cumulative, unknownDeviceRows: 0 } as DailyChange
}

/** 2026-03-01 .. 2026-03-08, every day delivered except 03-06. */
const CHANGES: DailyChange[] = [
  day('2026-03-01', 10, 4, 1006),
  day('2026-03-02', 20, 5, 1021),
  day('2026-03-03', 30, 6, 1045),
  day('2026-03-04', 40, 7, 1078),
  day('2026-03-05', 50, 8, 1120),
  // 2026-03-06 never delivered
  day('2026-03-07', 70, 10, 1180),
  day('2026-03-08', 80, 11, 1249),
]

const CHURN: DailyChurn[] = [
  { date: '2026-03-01', simChanges: 1, deviceChanges: 10 },
  { date: '2026-03-02', simChanges: 2, deviceChanges: 20 },
  { date: '2026-03-03', simChanges: 3, deviceChanges: 30 },
  { date: '2026-03-04', simChanges: 4, deviceChanges: 40 },
]

const BASE: Pick<PeriodComparisonConfig, 'metric' | 'aggregate' | 'baseline' | 'baselineFrom' | 'baselineTo'> = {
  metric: 'added',
  aggregate: 'total',
  baseline: 'previous',
  baselineFrom: null,
  baselineTo: null,
}

describe('period comparison', () => {
  it('takes the previous period as the same number of calendar days ending the day before', () => {
    expect(previousWindow({ from: '2026-03-05', to: '2026-03-08' })).toEqual({ from: '2026-03-01', to: '2026-03-04' })
    // Across a month end, in UTC.
    expect(previousWindow({ from: '2026-03-01', to: '2026-03-02' })).toEqual({ from: '2026-02-27', to: '2026-02-28' })
  })

  it('sums a flow over delivered days and never fills a missing day in', () => {
    const f = periodFigure(CHANGES, { from: '2026-03-05', to: '2026-03-08' }, 'added', 'total')
    expect(f).toMatchObject({ calendarDays: 4, deliveredDays: 3, value: 50 + 70 + 80 })
  })

  it('averages a flow per delivered day, not per calendar day', () => {
    const f = periodFigure(CHANGES, { from: '2026-03-05', to: '2026-03-08' }, 'added', 'dailyAverage')
    expect(f.value).toBe(200 / 3)
  })

  it('reads population as a level: the last delivered day, or the mean over delivered days', () => {
    const window = { from: '2026-03-05', to: '2026-03-08' }
    expect(periodFigure(CHANGES, window, 'population', 'total').value).toBe(1249)
    expect(periodFigure(CHANGES, window, 'population', 'dailyAverage').value).toBe((1120 + 1180 + 1249) / 3)
  })

  it('compares with the previous period and flags a total over unequal delivered days', () => {
    const c = comparePeriods({ config: BASE, current: { from: '2026-03-05', to: '2026-03-08' }, rows: CHANGES, dataStart: '2026-03-01' })
    expect(c.unavailable).toBeNull()
    expect(c.baseline).toMatchObject({ from: '2026-03-01', to: '2026-03-04', deliveredDays: 4, value: 100 })
    expect(c.current.value).toBe(200)
    expect(c.absolute).toBe(100)
    expect(c.percent).toBe(100)
    expect(c.uneven).toBe(true)
  })

  it('does not flag unequal days for an average or for population', () => {
    const current = { from: '2026-03-05', to: '2026-03-08' }
    expect(comparePeriods({ config: { ...BASE, aggregate: 'dailyAverage' }, current, rows: CHANGES, dataStart: null }).uneven).toBe(false)
    expect(comparePeriods({ config: { ...BASE, metric: 'population' }, current, rows: CHANGES, dataStart: null }).uneven).toBe(false)
  })

  it('reads SIM and handset changes from the churn series', () => {
    const c = comparePeriods({
      config: { ...BASE, metric: 'deviceChanges' },
      current: { from: '2026-03-03', to: '2026-03-04' },
      rows: CHURN,
      dataStart: '2026-03-01',
    })
    expect(c.baseline?.value).toBe(30)
    expect(c.current.value).toBe(70)
  })

  it('says a baseline wholly before the first daily file is unavailable rather than zero', () => {
    const c = comparePeriods({ config: BASE, current: { from: '2026-03-01', to: '2026-03-04' }, rows: CHANGES, dataStart: '2026-03-01' })
    expect(c.unavailable).toMatch(/before the first daily file/)
    expect(c.absolute).toBeNull()
  })

  it('needs both custom dates, in order', () => {
    const current = { from: '2026-03-05', to: '2026-03-08' }
    const custom = { ...BASE, baseline: 'custom' as const }
    expect(comparePeriods({ config: { ...custom, baselineFrom: '2026-03-01' }, current, rows: CHANGES, dataStart: null }).unavailable).toMatch(/both/)
    expect(
      comparePeriods({ config: { ...custom, baselineFrom: '2026-03-04', baselineTo: '2026-03-01' }, current, rows: CHANGES, dataStart: null }).unavailable,
    ).toMatch(/starts after it ends/)
    const ok = comparePeriods({ config: { ...custom, baselineFrom: '2026-03-01', baselineTo: '2026-03-02' }, current, rows: CHANGES, dataStart: null })
    expect(ok.baseline?.value).toBe(30)
  })

  it('has no percentage for a change from zero', () => {
    const rows = [day('2026-03-01', 0, 0, 0), day('2026-03-02', 5, 0, 5)]
    const c = comparePeriods({ config: BASE, current: { from: '2026-03-02', to: '2026-03-02' }, rows, dataStart: '2026-03-01' })
    expect(c.absolute).toBe(5)
    expect(c.percent).toBeNull()
  })

  it('takes the size of a negative baseline for the percentage, so a smaller loss reads as a rise', () => {
    const rows = [day('2026-03-01', 0, 10, 990), day('2026-03-02', 0, 5, 985)]
    const c = comparePeriods({ config: { ...BASE, metric: 'net' }, current: { from: '2026-03-02', to: '2026-03-02' }, rows, dataStart: '2026-03-01' })
    expect(c.absolute).toBe(5)
    expect(c.percent).toBe(50)
  })
})
