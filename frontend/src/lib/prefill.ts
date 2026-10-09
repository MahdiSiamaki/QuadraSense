/**
 * Hands a value from a dashboard to a lookup page without putting it in the URL.
 *
 * It travels in the router's history state, which the browser keeps for that one history entry and
 * never sends anywhere: not to the server, not in a Referer, not in a link someone copies. A number,
 * SIM or handset in a query string reaches access logs and browser history - the reason every lookup
 * in this product is a POST and every drill-down a panel.
 *
 * Read once: the page takes the value and removes it from the entry, so Back, a reload or a restored
 * tab never runs the lookup - an audited request - a second time.
 */
const KEY = 'dashboardPrefill'

/** The `state` for `router.push({ name, state })`. */
export function prefillState(value: string): Record<string, string> {
  return { [KEY]: value }
}

/** The handed-over value, or null; removed from the history entry as it is read. */
export function takePrefill(): string | null {
  const state = window.history.state as Record<string, unknown> | null
  const value = state?.[KEY]
  if (typeof value !== 'string' || value.trim() === '') return null
  const rest = { ...state }
  delete rest[KEY]
  window.history.replaceState(rest, '')
  return value.trim()
}
