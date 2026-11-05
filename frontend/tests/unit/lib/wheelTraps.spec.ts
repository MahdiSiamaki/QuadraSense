import { describe, expect, it, vi } from 'vitest'
import { leaveWheelToPage } from '@/lib/chart-zoom'

/*
  Two ways page content can keep the mouse wheel from the page, found in the sources because the
  test DOM neither scrolls nor loads Tailwind or a canvas.

  `overscroll-contain` stops the wheel at the element. Since Chrome 144 it does so on any scroll
  container, overflowing or not, so every dashboard card held the wheel even with nothing to scroll.
  ECharts' inside zoom cancels the wheel over the plot. Both were measured on the real dashboard on
  2026-11-05: 0 px of page movement under the pointer, 300 px with the cause removed.

  An overlay is the one place containment belongs: the page behind it must not move.
*/
const vue = import.meta.glob('/src/**/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

const OVERLAYS = new Set([
  '/src/features/dashboard/components/ActionMenu.vue',
  '/src/features/dashboard/components/AddWidgetDialog.vue',
  '/src/features/dashboard/components/DashboardSwitcher.vue',
  '/src/features/dashboard/components/ShareDialog.vue',
])

describe('the page keeps the wheel', () => {
  it('uses overscroll containment only on overlays', () => {
    const found = Object.entries(vue)
      .filter(([file, source]) => source.includes('overscroll-contain') && !OVERLAYS.has(file))
      .map(([file]) => file)
    expect(found).toEqual([])
  })

  it('lists only overlays that still need it', () => {
    const stale = [...OVERLAYS].filter((file) => !vue[file]?.includes('overscroll-contain'))
    expect(stale).toEqual([])
  })

  it('has every chart with an inside zoom leave the wheel to the page', () => {
    const zooming = Object.entries(vue).filter(([, source]) => /type:\s*'inside'/.test(source))
    expect(zooming.length).toBeGreaterThanOrEqual(4)
    expect(zooming.filter(([, source]) => !source.includes('leaveWheelToPage(')).map(([file]) => file)).toEqual([])
  })
})

describe('leaveWheelToPage', () => {
  function chart() {
    const outer = document.createElement('div')
    const plot = document.createElement('canvas')
    outer.append(plot)
    const reached = vi.fn()
    plot.addEventListener('wheel', reached)
    leaveWheelToPage(outer)
    return { plot, reached }
  }

  it('keeps a plain wheel from the chart, so the chart cannot cancel it', () => {
    const { plot, reached } = chart()
    plot.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true }))
    expect(reached).not.toHaveBeenCalled()
  })

  it('lets Ctrl + wheel through to zoom, and a touchpad pinch with it', () => {
    const { plot, reached } = chart()
    const wheel = new WheelEvent('wheel', { bubbles: true, cancelable: true })
    // happy-dom's WheelEvent drops ctrlKey from its init.
    Object.defineProperty(wheel, 'ctrlKey', { value: true })
    plot.dispatchEvent(wheel)
    expect(reached).toHaveBeenCalledOnce()
  })
})
