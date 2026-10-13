import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent, h, inject } from 'vue'
import WidgetFrame from '@/features/dashboard/components/WidgetFrame.vue'
import { FIT_CONTENT } from '@/features/dashboard/composables/fitContent'
import { FRAME_ACTIONS } from '@/features/dashboard/components/frameActions'

/*
  What the frame gives the grid and the reader: a content-sized card hands over the element that has
  the content's own height; a bare card (a KPI row) keeps its menu beside it, not on a strip above.
*/
const menu = [{ key: 'export:csv', label: 'Download CSV' }]

describe('the widget frame', () => {
  it('hands a content-sized card to the grid while it is mounted, and only then', () => {
    const registry = { observe: vi.fn(), unobserve: vi.fn() }
    const wrapper = mount(WidgetFrame, {
      props: { title: 'Top vendors', fit: true },
      slots: { default: () => h('p', 'bars') },
      global: { provide: { [FIT_CONTENT as symbol]: registry } },
    })
    return wrapper.vm.$nextTick().then(() => {
      const content = wrapper.get('[data-fit-content]').element
      expect(content.textContent).toBe('bars')
      expect(registry.observe).toHaveBeenCalledWith(content)
      wrapper.unmount()
      expect(registry.unobserve).toHaveBeenCalledWith(content)
    })
  })

  it('wraps nothing for a card with a fixed height', () => {
    const registry = { observe: vi.fn(), unobserve: vi.fn() }
    const wrapper = mount(WidgetFrame, {
      props: { title: 'Daily change' },
      slots: { default: () => h('p', 'chart') },
      global: { provide: { [FIT_CONTENT as symbol]: registry } },
    })
    expect(wrapper.find('[data-fit-content]').exists()).toBe(false)
    expect(registry.observe).not.toHaveBeenCalled()
  })

  it("hands a bare card's menu to the card in view mode and draws neither a strip nor a menu beside it", () => {
    const Card = defineComponent({
      setup() {
        const actions = inject(FRAME_ACTIONS, null)
        return () => h('p', { class: 'card' }, actions?.value?.label ?? 'no menu')
      },
    })
    const wrapper = mount(WidgetFrame, { props: { title: 'Active bindings', chrome: 'bare', menu }, slots: { default: () => h(Card) } })
    expect(wrapper.get('.card').text()).toBe('Actions for Active bindings')
    // The card is the frame's whole content: full width, so its edges are the grid's.
    expect(wrapper.find('[aria-label="Actions for Active bindings"]').exists()).toBe(false)
    expect(wrapper.element.firstElementChild!.contains(wrapper.get('.card').element)).toBe(true)
  })

  it('keeps the strip in edit mode, where the drag handle needs it', () => {
    const wrapper = mount(WidgetFrame, {
      props: { title: 'Key figures', chrome: 'bare', menu, editing: true },
      slots: { default: () => h('p', { class: 'kpis' }, 'six cards') },
    })
    const strip = wrapper.element.firstElementChild!
    expect(strip.querySelector('.dashboard-drag-handle')).not.toBeNull()
    expect(strip.contains(wrapper.get('.kpis').element)).toBe(false)
  })
})
