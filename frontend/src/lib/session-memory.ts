/**
 * Small per-tab values that must not go in the URL, and must not outlive the signed-in session.
 *
 * The Devices search box takes phone numbers, IMSIs and IMEIs. In the query string they would
 * land in browser history, bookmarks, and every link someone copies. `sessionStorage` is per tab
 * and gone with it; signing out clears it too, so the next person at the same tab does not find
 * the last one's search waiting.
 *
 * Every access is guarded: storage can be unavailable (private windows, blocked site data), and
 * the page must then simply start empty.
 */

const PREFIX = 'sqm.session.'

export function recall(key: string): string {
  try {
    return sessionStorage.getItem(PREFIX + key) ?? ''
  } catch {
    return ''
  }
}

export function remember(key: string, value: string): void {
  try {
    if (value) sessionStorage.setItem(PREFIX + key, value)
    else sessionStorage.removeItem(PREFIX + key)
  } catch {
    // Not remembered; the page still works.
  }
}

/** Forgets everything this module stored. Called when the session ends. */
export function forgetAll(): void {
  try {
    for (let i = sessionStorage.length - 1; i >= 0; i--) {
      const key = sessionStorage.key(i)
      if (key?.startsWith(PREFIX)) sessionStorage.removeItem(key)
    }
  } catch {
    // Nothing could be stored either, so there is nothing to forget.
  }
}
