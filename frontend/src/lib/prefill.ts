/**
 * Hands a value from a dashboard to a lookup page without putting it in the URL - or in history.
 *
 * A number, SIM or handset in a query string reaches access logs and browser history - the reason
 * every lookup in this product is a POST and every drill-down a panel. This hand-off was first made
 * through the router's history state, removed as it was read; a review found that vue-router keeps its
 * own copy of an entry's state and writes it back on the next in-app navigation, so Back re-ran the
 * audited lookup and the identifier stayed in the tab's session history. It now lives only in memory:
 *
 * - **for one page**: the value names the route it is for, and no other page can take it;
 * - **once**: taking it clears it, so Back, Forward and a reload never run the lookup again;
 * - **briefly**: it expires after a few seconds, so a navigation that never reaches the page (refused
 *   for a missing permission, redirected) cannot leave it waiting for a later visit.
 */
const TTL_MS = 5_000

let pending: { route: string; value: string; at: number } | null = null

/** Hands `value` to the page at route `route`; call just before navigating there. */
export function handOver(route: string, value: string): void {
  const trimmed = value.trim()
  pending = trimmed === '' ? null : { route, value: trimmed, at: Date.now() }
}

/** The value handed to `route`, or null; cleared as it is read, and never older than a few seconds. */
export function takePrefill(route: string): string | null {
  const handed = pending
  if (handed === null || handed.route !== route) return null
  pending = null
  return Date.now() - handed.at > TTL_MS ? null : handed.value
}
