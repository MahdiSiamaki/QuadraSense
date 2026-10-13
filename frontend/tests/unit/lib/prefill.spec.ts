import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { handOver, takePrefill } from '@/lib/prefill'

describe('dashboard prefill', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    window.history.replaceState(null, '')
    takePrefill('lookup')
    takePrefill('imsi-search')
  })
  afterEach(() => vi.useRealTimers())

  it('hands a value over in memory: never in the URL, never in history state', () => {
    const before = window.location.href
    handOver('lookup', '9121234567')
    expect(window.location.href).toBe(before)
    expect(JSON.stringify(window.history.state ?? {})).not.toContain('9121234567')
    expect(takePrefill('lookup')).toBe('9121234567')
  })

  it('is read once: Back, a reload or a later visit does not run the lookup again', () => {
    handOver('imsi-search', '432111234567890')
    expect(takePrefill('imsi-search')).toBe('432111234567890')
    expect(takePrefill('imsi-search')).toBeNull()
  })

  it('is for one page: another page cannot take it, and it waits for its own', () => {
    handOver('lookup', '9121234567')
    expect(takePrefill('relationships')).toBeNull()
    expect(takePrefill('lookup')).toBe('9121234567')
  })

  it('expires, so a navigation refused on the way cannot leave it for a later visit', () => {
    handOver('lookup', '9121234567')
    vi.advanceTimersByTime(6_000)
    expect(takePrefill('lookup')).toBeNull()
  })

  it('ignores an empty value', () => {
    handOver('lookup', '   ')
    expect(takePrefill('lookup')).toBeNull()
  })
})
