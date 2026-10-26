import { describe, expect, it } from 'vitest'
import { createRateMeter, timeLeftMs } from '@/lib/transfer-rate'
import { formatRate, formatTimeLeft } from '@/lib/format'

describe('the rate meter', () => {
  it('says nothing until it has seen enough time', () => {
    const meter = createRateMeter()
    meter.sample(0, 0)
    expect(meter.rate()).toBeNull()
    meter.sample(1_000_000, 1_000)
    expect(meter.rate()).toBeNull()
    meter.sample(2_000_000, 2_000)
    expect(meter.rate()).toBe(1_000_000)
  })

  it('measures the last few seconds, not the average since the start', () => {
    const meter = createRateMeter(8_000)
    // Fast for twenty seconds, then the connection slows to a tenth.
    for (let s = 0; s <= 20; s++) meter.sample(s * 1_000_000, s * 1_000)
    for (let s = 21; s <= 40; s++) meter.sample(20_000_000 + (s - 20) * 100_000, s * 1_000)

    // Since the start it would be ~0.55 MB/s; the last eight seconds say 0.1 MB/s.
    expect(meter.rate()).toBeCloseTo(100_000, -3)
  })

  it('reads a stalled transfer as zero, not as the speed before it stalled', () => {
    const meter = createRateMeter(8_000)
    for (let s = 0; s <= 10; s++) meter.sample(s * 500_000, s * 1_000)
    for (let s = 11; s <= 30; s++) meter.sample(5_000_000, s * 1_000)
    expect(meter.rate()).toBe(0)
  })
})

describe('time left', () => {
  it('follows from what is left and the rate, and says nothing without a rate', () => {
    expect(timeLeftMs(10_000_000, 1_000_000)).toBe(10_000)
    expect(timeLeftMs(10_000_000, null)).toBeNull()
    expect(timeLeftMs(10_000_000, 0)).toBeNull()
  })

  it('is said roughly, the way an estimate should be', () => {
    expect(formatTimeLeft(null)).toBeNull()
    expect(formatTimeLeft(42_000)).toBe('less than a minute left')
    expect(formatTimeLeft(4 * 60_000 + 20_000)).toBe('about 4 min left')
    expect(formatTimeLeft(60 * 60_000)).toBe('about 1 h left')
    expect(formatTimeLeft(95 * 60_000)).toBe('about 1 h 35 min left')
  })

  it('gives the speed in the units a file system reports', () => {
    expect(formatRate(4_404_019)).toBe('4.2 MB/s')
    expect(formatRate(130 * 1024)).toBe('130.0 KB/s')
  })
})
