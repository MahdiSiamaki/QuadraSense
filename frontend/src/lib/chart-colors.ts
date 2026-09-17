/**
 * Design tokens, resolved into a colour format the charting library can actually compute with.
 *
 * **The bug this exists to fix.** Every token in `tokens.css` is `oklch()`, and ECharts renders to
 * canvas, where the browser understands that string perfectly - so bars drew correctly and
 * everything looked fine. Hovering one made it *disappear*.
 *
 * The reason is that ECharts does not only paint the colour, it computes with it: the default
 * hover state is the base colour lifted a little, via zrender's own colour parser. That parser
 * handles hex, `rgb()`, `rgba()`, `hsl()` and `hsla()`, and returns `undefined` for anything else.
 * Measured directly against the installed version:
 *
 * ```
 * lift('#4f79e8')             -> 'rgba(86,133,255,1)'
 * lift('rgb(79,121,232)')     -> 'rgba(86,133,255,1)'
 * lift('oklch(58% 0.16 264)') -> undefined        <- the bar is filled with nothing
 * ```
 *
 * So the fill became `undefined` on hover and the bar vanished, on every chart, in both themes.
 *
 * **Why the conversion is done by the browser rather than by arithmetic.** oklch to sRGB is a
 * well-defined transform and also an easy one to get subtly wrong, especially at the gamut edge
 * where these tokens sit. Painting one pixel and reading it back asks the browser for the colour
 * it would actually have drawn - which is by definition the right answer, and stays right if the
 * tokens ever move to `color()`, `lab()` or anything else CSS grows.
 */

/**
 * Cache keyed by the raw token text.
 *
 * Keying on the raw value rather than the token name makes theme switching correct for free: dark
 * mode redefines `--viz-1` to a different `oklch(...)` string, so it is a different key and gets
 * its own entry rather than a stale one.
 */
const resolved = new Map<string, string>()

let probe: CanvasRenderingContext2D | null = null

function context(): CanvasRenderingContext2D | null {
  if (probe) return probe
  const canvas = document.createElement('canvas')
  canvas.width = 1
  canvas.height = 1
  probe = canvas.getContext('2d', { willReadFrequently: true })
  return probe
}

/**
 * Converts any CSS colour the browser understands into `rgb()` or `rgba()`.
 *
 * Exported for the charts that need to resolve a literal rather than a token.
 */
export function toRgb(css: string): string {
  if (!css) return css

  const cached = resolved.get(css)
  if (cached) return cached

  const ctx = context()
  if (!ctx) return css

  try {
    ctx.clearRect(0, 0, 1, 1)
    ctx.fillStyle = css
    ctx.fillRect(0, 0, 1, 1)

    // Indexing a Uint8ClampedArray is `number | undefined` under noUncheckedIndexedAccess, and a
    // 1x1 RGBA read always has exactly four bytes, so the defaults are unreachable rather than
    // meaningful.
    const pixel = ctx.getImageData(0, 0, 1, 1).data
    const r = pixel[0] ?? 0
    const g = pixel[1] ?? 0
    const b = pixel[2] ?? 0
    const a = pixel[3] ?? 0
    // A fully transparent result means the browser could not parse it either, and painting the
    // chart with transparent would reproduce the very bug this function exists to prevent.
    const value =
      a === 0
        ? css
        : a === 255
          ? `rgb(${r}, ${g}, ${b})`
          : `rgba(${r}, ${g}, ${b}, ${(a / 255).toFixed(3)})`

    resolved.set(css, value)
    return value
  } catch {
    // Canvas can be unavailable or tainted in odd environments. Returning the original keeps the
    // chart drawing in its base state, which is what happened before this function existed.
    return css
  }
}

/**
 * Reads a design token and returns it as `rgb()` / `rgba()`.
 *
 * Use this anywhere a token is handed to ECharts. Reading the custom property directly returns
 * the raw `oklch(...)` text, which paints correctly and then breaks on hover.
 */
export function chartColor(token: string): string {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(token).trim()
  return toRgb(raw)
}

/** Clears the cache. Only needed if the tokens themselves are edited at runtime. */
export function clearChartColorCache(): void {
  resolved.clear()
}
