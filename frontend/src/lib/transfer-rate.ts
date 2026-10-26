/**
 * How fast something is moving, measured over the last few seconds.
 *
 * Over the last few seconds, not since the start: an upload over the VPN runs at 80-330 KB/s and
 * stalls when the tunnel drops (docs/operations/deployment/02-department-vm.md), so an average
 * since the start would keep promising the speed of a minute ago. Over the last second only, the
 * figure would jump with every progress event and the time left with it.
 */
export interface RateMeter {
  /** Records the amount done so far - bytes, or any unit that only grows - at `now` (ms). */
  sample(done: number, now?: number): void
  /** Units per second over the window, or null until there is enough to say. */
  rate(): number | null
}

/** Samples older than this are forgotten. */
const WINDOW_MS = 8_000

/** Below this span a rate is a guess: two progress events 50 ms apart say little. */
const MIN_SPAN_MS = 1_500

export function createRateMeter(windowMs = WINDOW_MS): RateMeter {
  const samples: { at: number; done: number }[] = []

  return {
    sample(done, now = Date.now()) {
      samples.push({ at: now, done })
      // Keep the newest sample that is already outside the window: the rate then spans the
      // whole window rather than whatever happens to be left inside it.
      while (samples.length > 2 && samples[1]!.at <= now - windowMs) samples.shift()
    },
    rate() {
      if (samples.length < 2) return null
      const first = samples[0]!
      const last = samples[samples.length - 1]!
      const span = last.at - first.at
      if (span < MIN_SPAN_MS) return null
      return Math.max(0, (last.done - first.done) / (span / 1000))
    },
  }
}

/** Milliseconds to cover `remaining` at `perSecond`, or null when the rate says nothing. */
export function timeLeftMs(remaining: number, perSecond: number | null): number | null {
  if (perSecond === null || perSecond <= 0) return null
  return (Math.max(0, remaining) / perSecond) * 1000
}
