import { describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { h } from 'vue'
import type { GridItemHTMLElement } from 'gridstack'
import DashboardGrid from '@/features/dashboard/components/DashboardGrid.vue'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

/*
  What the grid tells GridStack about each card. Positions and gestures need a real layout and are
  proven in the browser; the limits a resize must stop at are plain values on each node.
*/
function widget(id: string, h: number, type = 'metric.kpi', y = 0): WidgetInstance {
  return {
    id,
    type,
    configVersion: CONTRACT.types[type]!.configVersion,
    title: null,
    subtitle: null,
    config: defaultConfig(type),
    overrides: {},
    layout: { x: 0, y, w: 12, h },
  } as WidgetInstance
}

function items(root: Element) {
  return Array.from(root.querySelectorAll<GridItemHTMLElement>('.grid-stack-item'))
}

function item(root: Element, id: string) {
  const found = items(root).find((el) => el.gridstackNode?.id === id)
  if (!found) throw new Error(`no grid item ${id}`)
  return found
}

describe('the dashboard grid', () => {
  it("stops a resize at the contract's tallest card, so a drag cannot make a height Save refuses", async () => {
    const wrapper = mount(DashboardGrid, {
      props: { widgets: [widget('w_grid0001', 4)], editable: true },
      slots: { item: () => h('div') },
      attachTo: document.body,
    })
    await flushPromises()

    const items = Array.from(wrapper.element.querySelectorAll<GridItemHTMLElement>('.grid-stack-item'))
    expect(items).toHaveLength(1)
    expect(items[0]!.gridstackNode?.maxH).toBe(CONTRACT.limits.maxWidgetHeight)
    wrapper.unmount()
  })

  it('sizes a KPI row or the freshness strip to its content, and leaves a chart at its layout height', async () => {
    const wrapper = mount(DashboardGrid, {
      props: {
        widgets: [widget('w_grid0001', 3, 'metric.kpiSummary'), widget('w_grid0002', 4, 'ops.freshness', 3), widget('w_grid0003', 10, 'change.daily', 7)],
        editable: false,
      },
      slots: { item: () => h('div') },
      attachTo: document.body,
    })
    await flushPromises()

    const [row, freshness, chart] = ['w_grid0001', 'w_grid0002', 'w_grid0003'].map((id) => item(wrapper.element, id))
    expect(row!.gridstackNode?.sizeToContent).toBe(true)
    expect(freshness!.gridstackNode?.sizeToContent).toBe(true)
    expect(chart!.gridstackNode?.sizeToContent).toBeFalsy()

    // The element GridStack measures keeps its own height for a content-sized card, and fills a fixed one.
    expect(row!.querySelector('.grid-stack-item-content > .dashboard-fit')).not.toBeNull()
    expect(chart!.querySelector('.grid-stack-item-content > .h-full')).not.toBeNull()
    expect(chart!.querySelector('.dashboard-fit')).toBeNull()
    wrapper.unmount()
  })

  it("never writes the stored height back over a content-sized card's own", async () => {
    const wrapper = mount(DashboardGrid, {
      props: { widgets: [widget('w_grid0001', 3, 'metric.kpiSummary'), widget('w_grid0003', 10, 'change.daily', 3)], editable: false },
      slots: { item: () => h('div') },
      attachTo: document.body,
    })
    await flushPromises()
    const row = item(wrapper.element, 'w_grid0001')
    // As resizeToContent would after measuring two rows of KPI cards.
    row.gridstackNode!.grid!.update(row, { h: 5 })

    // Any change to the dashboard re-syncs every card.
    await wrapper.setProps({ widgets: [widget('w_grid0001', 3, 'metric.kpiSummary'), widget('w_grid0003', 10, 'change.daily', 4)] })
    await flushPromises()
    expect(item(wrapper.element, 'w_grid0001').gridstackNode?.h).toBe(5)
    wrapper.unmount()
  })
})
