import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ColumnHeading, { splitHeading } from '@/features/risk/ColumnHeading.vue'

/* The risk lists' headings: what is counted on one line, its window or unit beneath. */
describe('a column heading', () => {
  it.each([
    ['Days with a SIM change, 7 days', 'Days with a SIM change', '7 days'],
    ['Days with a SIM change, before screens', 'Days with a SIM change', 'before screens'],
    ['IMEIs added, 30 days, before screens', 'IMEIs added', '30 days, before screens'],
    ['Number (MSISDN)', 'Number', 'MSISDN'],
    ['Threshold (more than)', 'Threshold', 'more than'],
    ['Last SIM change', 'Last SIM change', null],
    ['On', 'On', null],
  ])('%s', (label, main, detail) => {
    expect(splitHeading(label)).toEqual({ main, detail })
  })

  it('keeps the whole label as its tooltip, so nothing the server says is lost', () => {
    const w = mount(ColumnHeading, { props: { label: 'IMEIs added, 30 days, before screens' } })
    expect(w.attributes('title')).toBe('IMEIs added, 30 days, before screens')
    expect(w.findAll('span span').map((s) => s.text())).toEqual(['IMEIs added', '30 days, before screens'])
  })
})
