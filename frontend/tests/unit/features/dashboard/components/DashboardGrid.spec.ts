import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { h } from 'vue'
import type { GridItemHTMLElement } from 'gridstack'
import DashboardGrid from '@/features/dashboard/components/DashboardGrid.vue'
import WidgetFrame from '@/features/dashboard/components/WidgetFrame.vue'
import { getWidget } from '@/features/dashboard/registry'
import { CONTRACT, defaultConfig } from '@/features/dashboard/registry/contract'
import type { WidgetInstance } from '@/features/dashboard/types/definition'

/*
  What the grid tells GridStack about each card. Positions and gestures need a real layout and are
  proven in the browser; the limits a resize must stop at are plain values on each node, and how a
  content-sized card is fitted is arithmetic over the heights it reads, which a test can set.
*/
function widget(id: string, h: number, type = 'metric.kpi', y = 0, x = 0, w = 12): WidgetInstance {
  return {
    id,
    type,
    configVersion: CONTRACT.types[type]!.configVersion,
    title: null,
    subtitle: null,
    config: defaultConfig(type),
    overrides: {},
    layout: { x, y, w, h },
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

/** A card the way the page draws one: in its frame, content-sized when its type says so. */
function frame({ widget: w }: { widget: WidgetInstance }) {
  return [h(WidgetFrame, { title: w.id, fit: getWidget(w.type)?.height === 'content' }, () => h('div', { class: 'content' }))]
}

/** Lays a card out as a browser would: the item's inner box, the frame body's room, the content's own height. */
function lay(el: GridItemHTMLElement, box: number, room: number, content: number) {
  Object.defineProperty(el.querySelector('.grid-stack-item-content')!, 'clientHeight', { configurable: true, value: box })
  const wrapper = el.querySelector<HTMLElement>('[data-fit-content]')!
  Object.defineProperty(wrapper.parentElement!, 'clientHeight', { configurable: true, value: room })
  wrapper.getBoundingClientRect = () => ({ height: content }) as DOMRect
}

/** The grid picks its column count from its width, which the test DOM does not lay out: a desktop one. */
function atDesktopWidth() {
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1200 })
}

describe('the dashboard grid', () => {
  afterEach(() => {
    delete (HTMLElement.prototype as unknown as Record<string, unknown>).clientWidth
  })

  it("stops a resize at the contract's tallest card, so a drag cannot make a height Save refuses", async () => {
    const wrapper = mount(DashboardGrid, {
      props: { widgets: [widget('w_grid0001', 4)], editable: true },
      slots: { item: () => h('div') },
      attachTo: document.body,
    })
    await flushPromises()

    const node = items(wrapper.element)[0]!.gridstackNode!
    // In pixels, whatever unit the grid counts in: 40 rows of 40px.
    expect(node.maxH! * node.grid!.getCellHeight()).toBe(CONTRACT.limits.maxWidgetHeight * 40)
    wrapper.unmount()
  })

  it('fits each content-sized card to its content, and every card on a row to the tallest', async () => {
    atDesktopWidth()
    const wrapper = mount(DashboardGrid, {
      props: {
        widgets: [
          widget('w_vendors', 9, 'device.top', 0, 0, 6),
          widget('w_models', 9, 'device.top', 0, 6, 6),
          widget('w_trend', 10, 'change.daily', 9),
        ],
        editable: false,
      },
      slots: { item: frame },
      attachTo: document.body,
    })
    await flushPromises()
    const vendors = item(wrapper.element, 'w_vendors')
    const models = item(wrapper.element, 'w_models')
    const cell = vendors.gridstackNode!.grid!.getCellHeight()

    // Vendors' content overflows its body by 100px; models' leaves 50px unused.
    lay(vendors, 340, 300, 400)
    lay(models, 340, 300, 250)
    ;(wrapper.vm as unknown as { refit: () => void }).refit()

    // Vendors needs 340 + 100 = 440px inside, 460px with the gap; models would need 290px, but a row ends together.
    expect(vendors.gridstackNode!.h! * cell).toBe(460)
    expect(models.gridstackNode!.h! * cell).toBe(460)
    // A fixed card keeps its layout height.
    expect(item(wrapper.element, 'w_trend').gridstackNode!.h! * cell).toBe(10 * 40)
    wrapper.unmount()
  })

  it('wastes less than a stored row on a content-sized card: the grid counts in quarters of one', async () => {
    const wrapper = mount(DashboardGrid, {
      props: { widgets: [widget('w_freshness', 4, 'ops.freshness')], editable: false },
      slots: { item: frame },
      attachTo: document.body,
    })
    await flushPromises()
    const freshness = item(wrapper.element, 'w_freshness')
    const cell = freshness.gridstackNode!.grid!.getCellHeight()

    // 190px of content: 210px with the gap, which whole 40px rows would round up to 240.
    lay(freshness, 140, 140, 190)
    ;(wrapper.vm as unknown as { refit: () => void }).refit()
    expect(freshness.gridstackNode!.h! * cell).toBe(210)
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
    // As a refit would after measuring two rows of KPI cards.
    row.gridstackNode!.grid!.update(row, { h: 21 })

    // Any change to the dashboard re-syncs every card.
    await wrapper.setProps({ widgets: [widget('w_grid0001', 3, 'metric.kpiSummary'), widget('w_grid0003', 10, 'change.daily', 4)] })
    await flushPromises()
    expect(item(wrapper.element, 'w_grid0001').gridstackNode?.h).toBe(21)
    wrapper.unmount()
  })
})
