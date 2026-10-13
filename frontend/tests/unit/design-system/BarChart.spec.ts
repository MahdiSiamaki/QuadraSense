import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import BarChart from '@/design-system/BarChart.vue'

// The chart's height is the component's; drawing it is ECharts', which needs a canvas the test DOM has not.
vi.mock('echarts/core', () => ({
  use: () => {},
  init: () => ({ setOption: () => {}, on: () => {}, resize: () => {}, dispose: () => {} }),
}))

const bars = (n: number) => Array.from({ length: n }, (_, i) => ({ key: `Vendor ${i}`, count: 100 - i, percent: 1 }))

describe('the bar chart', () => {
  it('gives every bar a 30px row when it sizes itself, so ten bars are never crowded into what a card had left', () => {
    const ten = mount(BarChart, { props: { data: bars(10), height: 'auto' } })
    expect((ten.element as HTMLElement).style.height).toBe(`${10 * 30 + 40}px`)
    const fifty = mount(BarChart, { props: { data: bars(50), height: 'auto' } })
    expect((fifty.element as HTMLElement).style.height).toBe(`${50 * 30 + 40}px`)
  })

  it('keeps a height it is given', () => {
    expect((mount(BarChart, { props: { data: bars(10), height: '18rem' } }).element as HTMLElement).style.height).toBe('18rem')
  })
})
