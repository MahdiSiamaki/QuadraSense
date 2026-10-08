/*
  Applies the reader's theme before the first paint.

  The app sets data-theme when its module bundle runs, which is after the browser has painted the
  page once in the light palette: a reader who chose dark saw a light flash on every load. This
  runs first, from <head>, synchronously. It is a file rather than an inline script because the
  security model allows no inline script (docs/architecture/04-security-model.md: default-src
  'self', no unsafe-inline). Keep the key and the rule in step with src/lib/theme.ts.
*/
;(function () {
  var mode = 'system'
  try {
    var stored = localStorage.getItem('sqm.theme')
    if (stored === 'light' || stored === 'dark' || stored === 'system') mode = stored
  } catch (e) {
    // Storage can throw in a locked-down browser; the system preference still applies.
  }
  var dark =
    mode === 'dark' ||
    (mode === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light')
})()
