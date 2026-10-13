import { describe, expect, it } from 'vitest'
import { kpiRowColumns } from '@/features/dashboard/widgets/overview/kpiRow'

/*
  The KPI row's columns, from its own width: one row when every card gets 10rem, otherwise even rows
  of three or two - the steps the dashboard page's row took - and never five cards and a stray one.
*/
describe('the KPI row', () => {
  it('puts six cards in one row only when each gets at least 10rem', () => {
    expect(kpiRowColumns(6, 6 * 160 + 5 * 12)).toBe(6)
    expect(kpiRowColumns(6, 6 * 160 + 5 * 12 - 1)).toBe(3)
  })

  it('falls back to rows of three, then two, then one - never 5 + 1', () => {
    expect(kpiRowColumns(6, 990)).toBe(3)
    expect(kpiRowColumns(6, 3 * 160 + 2 * 12)).toBe(3)
    expect(kpiRowColumns(6, 350)).toBe(2)
    expect(kpiRowColumns(6, 300)).toBe(1)
  })

  it('takes as many columns as there are cards, never more', () => {
    expect(kpiRowColumns(1, 1000)).toBe(1)
    expect(kpiRowColumns(2, 600)).toBe(2)
    expect(kpiRowColumns(3, 400)).toBe(2)
  })

  it('draws one row before the width is known', () => {
    expect(kpiRowColumns(6, 0)).toBe(6)
  })
})
