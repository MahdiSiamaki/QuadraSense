/*
  Control styles the Explorer's editors share, so a select in a condition and a select in a
  measure look like one family. Tokens only - no literal colours.
*/

// max-w-full: a select is as wide as its longest option - 'Device for the Automatic Processing of
// Data (APD)' - and pushed a 320px page sideways.
export const control =
  'max-w-full rounded-[var(--radius-md)] border bg-[var(--c-surface)] px-2 py-1 text-xs text-[var(--c-text)] ' +
  'disabled:opacity-60 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-[var(--c-accent)]'

export const mono = `${control} tabular font-mono tracking-wide`

export const miniLabel = 'block text-2xs font-medium text-[var(--c-text-muted)]'

export const iconButton =
  'grid size-7 shrink-0 place-items-center rounded-[var(--radius-md)] text-[var(--c-text-muted)] ' +
  'hover:bg-[var(--c-surface-hover)] hover:text-[var(--c-danger)] ' +
  'focus-visible:outline-2 focus-visible:outline-[var(--c-accent)]'

/** A two- or three-way choice drawn as joined buttons. */
export const segment = (on: boolean): string =>
  'px-2.5 py-1 text-xs font-medium transition-colors first:rounded-l-[var(--radius-md)] ' +
  'last:rounded-r-[var(--radius-md)] focus-visible:outline-2 focus-visible:outline-[var(--c-accent)] ' +
  (on
    ? 'bg-[var(--c-accent-subtle)] text-[var(--c-accent)]'
    : 'bg-[var(--c-surface)] text-[var(--c-text-secondary)] hover:bg-[var(--c-surface-hover)]')

/** Field types whose values are identifiers or codes: digits, set in monospace. */
export const DIGIT_TYPES = new Set(['Msisdn', 'Imsi', 'Imei', 'Tac'])
