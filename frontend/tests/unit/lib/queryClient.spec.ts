import { describe, expect, it } from 'vitest'
import { ApiError } from '@/api/client'
import { shouldRetry } from '@/lib/queryClient'

/*
  One more try only where it can help. A 504 is the same slow query again; a 4xx is an answer, and
  a refused request is audited - a retry writes the denial twice.
*/
describe('retrying a failed query', () => {
  it('never sends a timed-out query again', () => {
    expect(shouldRetry(0, new ApiError(504, null, null))).toBe(false)
  })

  it('never repeats a request the server answered with a 4xx', () => {
    expect(shouldRetry(0, new ApiError(403, null, null))).toBe(false)
    expect(shouldRetry(0, new ApiError(404, null, null))).toBe(false)
  })

  it('tries once more after a network failure or another 5xx, and only once', () => {
    expect(shouldRetry(0, new TypeError('Failed to fetch'))).toBe(true)
    expect(shouldRetry(0, new ApiError(503, null, null))).toBe(true)
    expect(shouldRetry(1, new ApiError(503, null, null))).toBe(false)
  })
})
