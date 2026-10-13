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
function widget(id: string, h: number): WidgetInstance {
  return {
    id,
    type: 'metric.kpi',
    configVersion: CONTRACT.types['metric.kpi']!.configVersion,
    title: null,
    subtitle: null,
    config: defaultConfig('metric.kpi'),
    overrides: {},
    layout: { x: 0, y: 0, w: 3, h },
  } as WidgetInstance
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
})
