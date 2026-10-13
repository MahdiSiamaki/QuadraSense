import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { api } from '@/api/client'
import AddWidgetDialog from '@/features/dashboard/components/AddWidgetDialog.vue'
import ConfigForm from '@/features/dashboard/components/ConfigForm.vue'
import { getWidget } from '@/features/dashboard/registry'
import { CONTRACT } from '@/features/dashboard/registry/contract'

/*
  Add widget: what the form starts from, what the preview runs with, and what may be added. Signed in
  with every permission; nothing here is about who may use a widget (WidgetHost.spec is).
*/
vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return { ...actual, useAuth: () => ({ can: () => true }) }
})

let wrapper: VueWrapper | null = null

async function openDialog() {
  wrapper = mount(AddWidgetDialog, {
    props: { open: false, widget: null, defaults: {}, urlLens: {}, widgetCount: 0, maxWidgets: 24 },
    global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

/** Chooses a catalogue card by its name and moves on, as a double click does. */
async function configure(w: VueWrapper, name: string) {
  const card = w.findAll('ul[aria-label="Widgets"] button').find((b) => b.find('.font-medium').text() === name)
  if (!card) throw new Error(`no "${name}" card`)
  await card.trigger('dblclick')
  await flushPromises()
}

async function back(w: VueWrapper) {
  await w.findAll('button').find((b) => b.text() === 'Back')!.trigger('click')
  await flushPromises()
}

function form(w: VueWrapper) {
  return w.findComponent(ConfigForm)
}

function addButton(w: VueWrapper) {
  return w.findAll('button').find((b) => b.text() === 'Add to dashboard')!
}

describe('the add widget dialog', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    // Nothing answers: these tests are about what is asked for, not what comes back.
    vi.spyOn(api, 'get').mockImplementation(() => new Promise(() => {}))
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.useRealTimers()
  })

  it("starts another preset of the same type from that preset's settings, not the last one's", async () => {
    const w = await openDialog()
    await configure(w, 'Shared IMEIs')
    expect(form(w).props('config').rule).toBe('SharedImeiSims30')

    await back(w)
    await configure(w, 'Repeated SIM changes')
    expect(form(w).props('config').rule).toBe('RepeatedSimChange7')
  })

  it('keeps what was typed when Back is followed by Continue on the same card', async () => {
    const w = await openDialog()
    await configure(w, 'Shared IMEIs')
    form(w).vm.$emit('update:title', 'IMEIs to look at')
    await flushPromises()

    await back(w)
    await configure(w, 'Shared IMEIs')
    expect(form(w).props('title')).toBe('IMEIs to look at')
  })

  it("previews a new form's own settings at once, never the last form's for half a second", async () => {
    const w = await openDialog()
    await configure(w, 'Shared IMEIs')
    await vi.advanceTimersByTimeAsync(600)
    expect(form(w).props('instance').config.rule).toBe('SharedImeiSims30')
    await back(w)
    await configure(w, 'Repeated SIM changes')

    // No timer has run since: the preview already belongs to this preset.
    expect(form(w).props('instance').config.rule).toBe('RepeatedSimChange7')
  })

  it("refuses a widget's own TAC that is not eight digits, as Save would", async () => {
    const w = await openDialog()
    await configure(w, getWidget('device.top')!.name)
    const tac = form(w).findAll('label').find((l) => l.text() === 'TAC')!
    const input = form(w).get(`#${tac.attributes('for')}`)

    await input.setValue('3500000')
    expect(form(w).text()).toContain('Eight digits.')
    expect(addButton(w).attributes('disabled')).toBeDefined()

    await input.setValue('35000000')
    expect(form(w).text()).not.toContain('Eight digits.')
    expect(addButton(w).attributes('disabled')).toBeUndefined()
  })

  it(`refuses a vendor longer than ${CONTRACT.lens.textFilterMaxLength} characters`, async () => {
    const w = await openDialog()
    await configure(w, getWidget('device.top')!.name)
    const vendor = form(w).findAll('label').find((l) => l.text() === 'Vendor')!
    await form(w).get(`#${vendor.attributes('for')}`).setValue('S'.repeat(CONTRACT.lens.textFilterMaxLength + 1))

    expect(form(w).text()).toContain(`At most ${CONTRACT.lens.textFilterMaxLength} characters.`)
    expect(addButton(w).attributes('disabled')).toBeDefined()
  })
})
