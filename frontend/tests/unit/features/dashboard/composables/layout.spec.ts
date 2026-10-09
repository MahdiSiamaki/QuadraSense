import { describe, expect, it } from 'vitest'
import {
  canMove,
  compact,
  deriveLayout,
  findSpot,
  move,
  overlaps,
  readingOrder,
  resolveCollisions,
  sizePreset,
} from '@/features/dashboard/composables/layout'
import { CONTRACT } from '@/features/dashboard/registry/contract'
import type { WidgetInstance } from '@/features/dashboard/types/definition'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'

function w(id: string, x: number, y: number, width: number, h: number, type = 'change.daily'): WidgetInstance {
  return { id, type, configVersion: 1, title: null, subtitle: null, config: {}, overrides: {}, layout: { x, y, w: width, h } }
}

function noOverlaps(widgets: WidgetInstance[]) {
  for (const a of widgets) for (const b of widgets) if (a !== b) expect(overlaps(a.layout, b.layout), `${a.id} / ${b.id}`).toBe(false)
}

const page = devicePopulation.definition.widgets

describe('reading order', () => {
  it('is top to bottom, then left to right', () => {
    expect(readingOrder([w('b', 6, 0, 6, 2), w('c', 0, 2, 12, 2), w('a', 0, 0, 6, 2)]).map((x) => x.id)).toEqual(['a', 'b', 'c'])
  })
})

describe('findSpot', () => {
  it('takes the first free place, row by row', () => {
    const widgets = [w('a', 0, 0, 6, 3), w('b', 6, 0, 4, 3)]
    expect(findSpot(widgets, 2, 2)).toEqual({ x: 10, y: 0, w: 2, h: 2 })
    expect(findSpot(widgets, 4, 2)).toEqual({ x: 0, y: 3, w: 4, h: 2 })
  })

  it('starts at the top of an empty dashboard', () => {
    expect(findSpot([], 12, 4)).toEqual({ x: 0, y: 0, w: 12, h: 4 })
  })
})

describe('compact', () => {
  it('lets every widget rise as far as it can, and changes nothing it was given', () => {
    const input = [w('a', 0, 5, 6, 2), w('b', 6, 9, 6, 2)]
    const before = JSON.stringify(input)
    const out = compact(input)
    expect(out.map((x) => x.layout.y)).toEqual([0, 0])
    expect(JSON.stringify(input)).toBe(before)
  })
})

describe('derived layouts', () => {
  it('stores 12 columns and returns them unchanged', () => {
    expect(deriveLayout(page, 12).map((x) => x.layout)).toEqual(page.map((x) => x.layout))
  })

  it('derives 6 columns with no overlaps, nothing past the edge and the input untouched', () => {
    const before = JSON.stringify(page)
    const tablet = deriveLayout(page, 6)
    noOverlaps(tablet)
    for (const x of tablet) expect(x.layout.x + x.layout.w).toBeLessThanOrEqual(6)
    expect(JSON.stringify(page)).toBe(before)
  })

  it('derives one column in reading order, heights kept', () => {
    const phone = deriveLayout(page, 1)
    noOverlaps(phone)
    const order = readingOrder(phone).map((x) => x.id)
    expect(order).toEqual(readingOrder(page).map((x) => x.id))
    for (const x of phone) {
      expect(x.layout.w).toBe(1)
      expect(x.layout.h).toBe(page.find((p) => p.id === x.id)!.layout.h)
    }
  })
})

describe('moving from the menu', () => {
  const row = [w('a', 0, 0, 4, 2), w('b', 4, 0, 4, 2), w('c', 8, 0, 4, 2), w('d', 0, 2, 12, 3)]

  it('moves one step in reading order and keeps every size', () => {
    const out = move(row, 'c', 'up')
    expect(readingOrder(out).map((x) => x.id)).toEqual(['a', 'c', 'b', 'd'])
    for (const x of out) expect(x.layout.w).toBe(row.find((r) => r.id === x.id)!.layout.w)
    noOverlaps(out)
  })

  it('moves to the start, the end, before and after another', () => {
    expect(readingOrder(move(row, 'd', 'start')).map((x) => x.id)).toEqual(['d', 'a', 'b', 'c'])
    expect(readingOrder(move(row, 'a', 'end')).map((x) => x.id)).toEqual(['b', 'c', 'd', 'a'])
    expect(readingOrder(move(row, 'c', { before: 'a' })).map((x) => x.id)).toEqual(['c', 'a', 'b', 'd'])
    expect(readingOrder(move(row, 'a', { after: 'c' })).map((x) => x.id)).toEqual(['b', 'c', 'a', 'd'])
  })

  it('knows when there is nowhere to go', () => {
    expect(canMove(row, 'a', 'up')).toBe(false)
    expect(canMove(row, 'd', 'down')).toBe(false)
    expect(canMove(row, 'b', 'up')).toBe(true)
  })
})

describe('size presets', () => {
  const spec = CONTRACT.types['change.daily']!

  it('never goes under the type minimum and never past the edge', () => {
    expect(sizePreset({ x: 0, y: 0, w: 12, h: 10 }, spec, 'small').w).toBe(spec.size.minW)
    expect(sizePreset({ x: 10, y: 0, w: 4, h: 10 }, spec, 'large')).toEqual({ x: 4, y: 0, w: 8, h: spec.size.h })
    expect(sizePreset({ x: 3, y: 0, w: 4, h: 10 }, spec, 'full').x).toBe(0)
  })

  it('a resize pushes whatever it now covers out of the way', () => {
    const out = resolveCollisions([w('a', 0, 0, 12, 2), w('b', 0, 2, 6, 2)].map((x) => (x.id === 'a' ? { ...x, layout: { ...x.layout, h: 4 } } : x)), 'a')
    noOverlaps(out)
    expect(out.find((x) => x.id === 'b')!.layout.y).toBe(4)
  })
})
