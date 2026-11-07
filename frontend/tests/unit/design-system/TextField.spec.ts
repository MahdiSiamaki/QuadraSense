import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import TextField from '@/design-system/TextField.vue'

/* A password's reveal button, and focus on a field that arrives with the next step of a form. */
describe('a revealable password field', () => {
  function field(modelValue = 'secret') {
    return mount(TextField, {
      props: { label: 'Password', type: 'password', revealable: true, modelValue },
    })
  }

  it('starts hidden, and says what the button will do', () => {
    const w = field()

    expect(w.find('input').attributes('type')).toBe('password')
    expect(w.find('button').attributes('aria-label')).toBe('Show password')
    expect(w.find('button').attributes('aria-pressed')).toBe('false')
  })

  it('shows and hides', async () => {
    const w = field()

    await w.find('button').trigger('click')
    expect(w.find('input').attributes('type')).toBe('text')
    expect(w.find('button').attributes('aria-label')).toBe('Hide password')
    expect(w.find('button').attributes('aria-pressed')).toBe('true')

    await w.find('button').trigger('click')
    expect(w.find('input').attributes('type')).toBe('password')
  })

  it('goes back to hidden when the field is cleared, as a failed sign-in clears it', async () => {
    const w = field()

    await w.find('button').trigger('click')
    await w.setProps({ modelValue: '' })
    await nextTick()

    expect(w.find('input').attributes('type')).toBe('password')
  })

  it('is a plain button that cannot submit the form it sits in', () => {
    expect(field().find('button').attributes('type')).toBe('button')
  })

  it('has no button unless asked for one', () => {
    const w = mount(TextField, { props: { label: 'Password', type: 'password', modelValue: '' } })

    expect(w.find('button').exists()).toBe(false)
  })
})

describe('a field that arrives with the next step', () => {
  it('takes focus when it is mounted, which the autofocus attribute alone would not do', () => {
    const w = mount(TextField, {
      props: { label: 'Code', modelValue: '', autofocus: true },
      attachTo: document.body,
    })

    expect(document.activeElement).toBe(w.find('input').element)
    w.unmount()
  })

  it('takes nothing when not asked to', () => {
    const w = mount(TextField, {
      props: { label: 'Code', modelValue: '' },
      attachTo: document.body,
    })

    expect(document.activeElement).not.toBe(w.find('input').element)
    w.unmount()
  })
})
