import { beforeEach, describe, expect, it } from 'vitest'
import { prefillState, takePrefill } from '@/lib/prefill'

describe('dashboard prefill', () => {
  beforeEach(() => window.history.replaceState(null, ''))

  it('hands a value over in history state, never in the URL', () => {
    const before = window.location.href
    window.history.replaceState({ ...prefillState('9121234567'), position: 3 }, '')
    expect(window.location.href).toBe(before)
    expect(takePrefill()).toBe('9121234567')
  })

  it('is read once: Back, a reload or a restored tab does not run the lookup again', () => {
    window.history.replaceState({ ...prefillState('432111234567890'), position: 3 }, '')
    expect(takePrefill()).toBe('432111234567890')
    expect(takePrefill()).toBeNull()
  })

  it('keeps the router’s own state when it removes the value', () => {
    window.history.replaceState({ ...prefillState('35000000'), position: 3, back: '/' }, '')
    takePrefill()
    expect(window.history.state).toEqual({ position: 3, back: '/' })
  })

  it('ignores an empty or missing value', () => {
    expect(takePrefill()).toBeNull()
    window.history.replaceState(prefillState('  '), '')
    expect(takePrefill()).toBeNull()
  })
})
