import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import ActionMenu from '@/features/dashboard/components/ActionMenu.vue'

/*
  The card menu is positioned against the viewport, so a card that scrolls its own body cannot clip
  it: a twelve-item edit menu in a three-row KPI card was cut off inside the card.
*/
const items = Array.from({ length: 12 }, (_, i) => ({ key: `k${i}`, label: `Action ${i}` }))

function rect(top: number, bottom: number, left = 600, right = 628): DOMRect {
  return { top, bottom, left, right, width: right - left, height: bottom - top, x: left, y: top, toJSON: () => ({}) } as DOMRect
}

describe('the action menu', () => {
  afterEach(() => vi.restoreAllMocks())

  it('opens below its trigger in fixed position, out of reach of any clipping card', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(rect(100, 128))
    const wrapper = mount(ActionMenu, { props: { label: 'Actions for KPI', items }, attachTo: document.body })
    await wrapper.get('button[aria-expanded]').trigger('click')
    await nextTick()

    const panel = wrapper.get('[id]:not(button)')
    expect(panel.classes()).toContain('fixed')
    expect(panel.attributes('style')).toContain('top: 132px')
    wrapper.unmount()
  })

  it('opens above its trigger when only the space above fits it', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(rect(window.innerHeight - 40, window.innerHeight - 12))
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockReturnValue(400)
    const wrapper = mount(ActionMenu, { props: { label: 'Actions for KPI', items }, attachTo: document.body })
    await wrapper.get('button[aria-expanded]').trigger('click')
    await nextTick()
    await nextTick()

    const style = wrapper.get('[id]:not(button)').attributes('style') ?? ''
    expect(style).toContain('bottom:')
    expect(style).not.toContain('top:')
    wrapper.unmount()
  })

  it('closes when the page scrolls, rather than float away from its trigger', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(rect(100, 128))
    const wrapper = mount(ActionMenu, { props: { label: 'Actions for KPI', items }, attachTo: document.body })
    await wrapper.get('button[aria-expanded]').trigger('click')
    expect(wrapper.find('[id]:not(button)').exists()).toBe(true)

    window.dispatchEvent(new Event('scroll'))
    await nextTick()
    expect(wrapper.find('[id]:not(button)').exists()).toBe(false)
    wrapper.unmount()
  })
})
