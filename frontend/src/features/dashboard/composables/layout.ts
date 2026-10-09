/**
 * Layout arithmetic for the dashboard grid (ADR-016 §5). Pure functions over widget instances: none
 * mutates its input, and each returns the same answer for the same widgets.
 *
 * Only the desktop layout (12 columns) is stored. Tablet (6) and phone (1) are derived from it here,
 * every time, in reading order - so a resized window can never write over the saved arrangement.
 *
 * Moves made from a menu or the keyboard (WCAG 2.2, 2.5.7: dragging has an alternative) change the
 * reading order and then lay the widgets out again as rows, left to right, keeping every widget's
 * size: predictable from the menu, where a free arrangement is what dragging is for.
 */
import type { WidgetInstance, WidgetLayout } from '../types/definition'
import { CONTRACT, typeSpec, type WidgetTypeSpec } from '../registry/contract'

export const COLUMNS = CONTRACT.limits.gridColumns

export type Columns = 12 | 6 | 1

export type MoveTarget = 'up' | 'down' | 'start' | 'end' | { before: string } | { after: string }

export type SizePreset = 'small' | 'medium' | 'large' | 'full'

/** Top to bottom, then left to right - the order a reader meets the widgets. */
export function readingOrder<T extends Pick<WidgetInstance, 'id' | 'layout'>>(widgets: readonly T[]): T[] {
  return [...widgets].sort((a, b) => a.layout.y - b.layout.y || a.layout.x - b.layout.x || a.id.localeCompare(b.id))
}

export function overlaps(a: WidgetLayout, b: WidgetLayout): boolean {
  return a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h
}

function collides(box: WidgetLayout, placed: readonly WidgetLayout[]): boolean {
  return placed.some((p) => overlaps(box, p))
}

function withLayout(widget: WidgetInstance, layout: WidgetLayout): WidgetInstance {
  return { ...widget, layout: { ...layout } }
}

/** The first position, row by row, where a w×h box fits without overlapping anything. */
export function findSpot(widgets: readonly WidgetInstance[], w: number, h: number, columns: number = COLUMNS): WidgetLayout {
  const width = Math.min(w, columns)
  const boxes = widgets.map((x) => x.layout)
  const bottom = boxes.reduce((max, b) => Math.max(max, b.y + b.h), 0)
  for (let y = 0; y <= bottom; y++) {
    for (let x = 0; x + width <= columns; x++) {
      const candidate = { x, y, w: width, h }
      if (!collides(candidate, boxes)) return candidate
    }
  }
  return { x: 0, y: bottom, w: width, h }
}

/** Top gravity: in reading order, each widget rises as far as it can without overlapping. */
export function compact(widgets: readonly WidgetInstance[]): WidgetInstance[] {
  const placed: WidgetLayout[] = []
  const moved = new Map<string, WidgetLayout>()
  for (const widget of readingOrder(widgets)) {
    const box = { ...widget.layout }
    while (box.y > 0 && !collides({ ...box, y: box.y - 1 }, placed)) box.y -= 1
    placed.push(box)
    moved.set(widget.id, box)
  }
  return widgets.map((w) => withLayout(w, moved.get(w.id)!))
}

/**
 * Pushes widgets down until nothing overlaps, the given widget keeping its place, then lets every
 * widget rise again. Used after a resize from the menu.
 */
export function resolveCollisions(widgets: readonly WidgetInstance[], fixedId: string): WidgetInstance[] {
  const fixed = widgets.find((w) => w.id === fixedId)
  if (!fixed) return compact(widgets)
  const placed: WidgetLayout[] = [{ ...fixed.layout }]
  const result = new Map<string, WidgetLayout>([[fixed.id, { ...fixed.layout }]])
  for (const widget of readingOrder(widgets)) {
    if (widget.id === fixedId) continue
    const box = { ...widget.layout }
    while (collides(box, placed)) box.y += 1
    placed.push(box)
    result.set(widget.id, box)
  }
  return compact(widgets.map((w) => withLayout(w, result.get(w.id)!)))
}

/** Lays widgets out as rows, left to right, in the given order, keeping each one's size. */
export function flow(ordered: readonly WidgetInstance[], columns: number = COLUMNS): WidgetInstance[] {
  let x = 0
  let y = 0
  let rowHeight = 0
  return ordered.map((widget) => {
    const w = Math.min(widget.layout.w, columns)
    if (x + w > columns) {
      y += rowHeight
      x = 0
      rowHeight = 0
    }
    const placed = withLayout(widget, { x, y, w, h: widget.layout.h })
    x += w
    rowHeight = Math.max(rowHeight, widget.layout.h)
    return placed
  })
}

/**
 * The layout at a breakpoint. 12: the stored layout. 6 (tablet): x and w halved, at least half the
 * type's minimum width, overlaps pushed down, then top gravity. 1 (phone): one column, reading order,
 * each widget's height kept.
 */
export function deriveLayout(widgets: readonly WidgetInstance[], columns: Columns): WidgetInstance[] {
  if (columns === 12) return widgets.map((w) => withLayout(w, w.layout))

  if (columns === 1) {
    let y = 0
    const stacked = new Map<string, WidgetLayout>()
    for (const widget of readingOrder(widgets)) {
      stacked.set(widget.id, { x: 0, y, w: 1, h: widget.layout.h })
      y += widget.layout.h
    }
    return widgets.map((w) => withLayout(w, stacked.get(w.id)!))
  }

  const placed: WidgetLayout[] = []
  const halved = new Map<string, WidgetLayout>()
  for (const widget of readingOrder(widgets)) {
    const minW = typeSpec(widget.type)?.size.minW ?? 1
    const w = Math.min(6, Math.max(Math.max(1, Math.ceil(minW / 2)), Math.round(widget.layout.w / 2)))
    let x = Math.floor(widget.layout.x / 2)
    if (x + w > 6) x = 6 - w
    const box = { x, y: widget.layout.y, w, h: widget.layout.h }
    while (collides(box, placed)) box.y += 1
    placed.push(box)
    halved.set(widget.id, box)
  }
  return compact(widgets.map((w) => withLayout(w, halved.get(w.id)!)))
}

/** Moves a widget in reading order, then lays all widgets out again as rows. */
export function move(widgets: readonly WidgetInstance[], id: string, target: MoveTarget): WidgetInstance[] {
  const order = readingOrder(widgets)
  const index = order.findIndex((w) => w.id === id)
  if (index < 0) return widgets.map((w) => withLayout(w, w.layout))

  const [widget] = order.splice(index, 1)
  let to: number
  if (target === 'up') to = Math.max(0, index - 1)
  else if (target === 'down') to = Math.min(order.length, index + 1)
  else if (target === 'start') to = 0
  else if (target === 'end') to = order.length
  else if ('before' in target) to = Math.max(0, order.findIndex((w) => w.id === target.before))
  else {
    const after = order.findIndex((w) => w.id === target.after)
    to = after < 0 ? order.length : after + 1
  }
  order.splice(to, 0, widget!)

  const flowed = new Map(flow(order).map((w) => [w.id, w.layout]))
  return widgets.map((w) => withLayout(w, flowed.get(w.id)!))
}

/** Whether a widget can move that way at all, for disabling menu items. */
export function canMove(widgets: readonly WidgetInstance[], id: string, target: 'up' | 'down'): boolean {
  const order = readingOrder(widgets)
  const index = order.findIndex((w) => w.id === id)
  return target === 'up' ? index > 0 : index >= 0 && index < order.length - 1
}

/** A size from the menu: small, medium, large or full width, never under the type's minimum. */
export function sizePreset(layout: WidgetLayout, spec: Pick<WidgetTypeSpec, 'size'>, preset: SizePreset): WidgetLayout {
  const wanted = { small: 3, medium: 6, large: 8, full: COLUMNS }[preset]
  const w = Math.min(COLUMNS, Math.max(spec.size.minW, wanted))
  const h = Math.max(spec.size.minH, spec.size.h)
  const x = layout.x + w > COLUMNS ? COLUMNS - w : layout.x
  return { x, y: layout.y, w, h }
}
