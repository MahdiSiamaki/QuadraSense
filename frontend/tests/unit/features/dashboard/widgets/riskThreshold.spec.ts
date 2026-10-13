import { describe, expect, it } from 'vitest'
import { thresholdProblem } from '@/features/dashboard/widgets/risk/threshold'

describe('risk list threshold', () => {
  const rule = { floor: 3, threshold: 5 }

  it('runs at the configured threshold', () => {
    expect(thresholdProblem(rule, null)).toBeNull()
  })

  it('accepts an override whose smallest listed value is stored: T + 1 >= floor', () => {
    expect(thresholdProblem(rule, 2)).toBeNull() // lists 3 and up; 3 is stored
    expect(thresholdProblem(rule, 100)).toBeNull()
  })

  it('refuses an override that would leave out values below the floor', () => {
    expect(thresholdProblem(rule, 1)).toMatch(/below 3 are not stored/) // would list 2, which is not stored
    expect(thresholdProblem(rule, 0)).not.toBeNull()
  })

  it('says an uncalibrated rule judges nothing, unless an override is given', () => {
    expect(thresholdProblem({ floor: 3, threshold: null }, null)).toMatch(/not calibrated/)
    expect(thresholdProblem({ floor: 3, threshold: null }, 4)).toBeNull()
  })
})
