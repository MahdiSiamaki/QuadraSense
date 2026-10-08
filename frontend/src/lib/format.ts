/**
 * Number and identifier formatting.
 *
 * Locale is fixed to en-US rather than the browser's: this is an English-only,
 * Gregorian-only product by decision, and a user whose OS is set to a locale
 * with different grouping separators should still see the same figure as the
 * colleague they are discussing it with.
 */

const LOCALE = 'en-US'

/**
 * Business dates are rendered in UTC, and that is a correctness rule rather than a preference.
 *
 * A business date is a calendar day with no time of day: `2026-07-02` is the day a file
 * describes, not an instant. Turning it into `2026-07-02T00:00:00Z` and then formatting it in
 * the reader's own zone shows the day BEFORE for everyone west of UTC - measured: New York, Los
 * Angeles and Honolulu all render that date as "Jul 1, 2026". A dashboard whose freshness card,
 * missing-day list and import history all shift by one day depending on who is looking is worse
 * than one that is merely wrong, because two colleagues comparing screens disagree.
 *
 * Timestamps are the opposite case and stay local: an import that ran at 08:31 happened at a
 * moment, and the reader wants it in their own time.
 */
const UTC = 'UTC'

const compact = new Intl.NumberFormat(LOCALE, {
  notation: 'compact',
  maximumFractionDigits: 1,
})

const full = new Intl.NumberFormat(LOCALE)

/** 125939523 -> "125.9M". For headline figures where magnitude is the point. */
export function formatCompact(value: number): string {
  return compact.format(value)
}

/** 125939523 -> "125,939,523". For tooltips, tables and exports. */
export function formatFull(value: number): string {
  return full.format(value)
}

/**
 * "12.3%". A share above zero that would round to zero reads "<0.1%" instead: "0.00%" beside a
 * count of 1 says the row is empty when it is not.
 */
export function formatPercent(value: number, digits = 1): string {
  const smallest = 10 ** -digits
  if (value > 0 && value < smallest / 2) return `<${smallest.toFixed(digits)}%`
  return `${value.toFixed(digits)}%`
}

/**
 * A signed decimal: "+12.34", "-9.30", "0.00". Grouped, so a model that grew from 3 bindings reads
 * "+41,233.33", and never "-0.00" for a change that rounds to nothing.
 */
export function formatSignedDecimal(value: number, digits: number): string {
  return new Intl.NumberFormat(LOCALE, {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
    signDisplay: 'exceptZero',
  }).format(value)
}

/** Signed, for net-change figures where direction carries the meaning. */
export function formatSigned(value: number): string {
  const sign = value > 0 ? '+' : ''
  return `${sign}${full.format(value)}`
}

/**
 * Groups a long identifier so it can be read and compared by eye.
 *
 * An IMEI is TAC(8) + serial(6); splitting on that boundary is not cosmetic,
 * it shows where the device model ends and the unit begins.
 */
export function formatImei(imei: string): string {
  if (imei.length !== 14) return imei
  return `${imei.slice(0, 8)} ${imei.slice(8)}`
}

/** MSISDN grouped for readability: 9140257910 -> "0914 025 7910". */
export function formatMsisdn(msisdn: string): string {
  if (msisdn.length !== 10) return msisdn
  return `0${msisdn.slice(0, 3)} ${msisdn.slice(3, 6)} ${msisdn.slice(6)}`
}

/**
 * Masks an identifier, keeping enough at each end to be recognisable.
 *
 * Masking is OFF by product decision, so this is not called on the normal path.
 * It exists because the capability was cheap to build now and would be a
 * refactor later if a regulator ever requires it - see PRIVACY_MASK_IDENTIFIERS.
 */
export function maskIdentifier(value: string, keepStart = 4, keepEnd = 3): string {
  if (value.length <= keepStart + keepEnd) return value
  return `${value.slice(0, keepStart)}${'*'.repeat(value.length - keepStart - keepEnd)}${value.slice(-keepEnd)}`
}

/**
 * Labels the time axis honestly.
 *
 * The delta files contain no date, so until the source supplies one the axis is
 * a delivery sequence number. Rendering a fabricated date here would be the
 * single easiest way to make the whole product quietly untrustworthy.
 */
export function formatSequence(sequence: number, dataDate: string | null): string {
  if (dataDate) {
    return new Intl.DateTimeFormat(LOCALE, {
      month: 'short',
      day: 'numeric',
      timeZone: UTC,
    }).format(new Date(dataDate))
  }
  return `#${sequence}`
}

/** 1073741824 -> "1.0 GB". Binary units, because that is what a file system reports. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`
  return `${(bytes / 1024 ** 3).toFixed(2)} GB`
}

/**
 * A duration in words, at one level of precision.
 *
 * "4m 12s" rather than "4 minutes and 12.31 seconds": the reader of an import list is
 * scanning for outliers, and every extra digit is one more thing to skip past.
 */
export function formatDuration(ms: number | null): string {
  if (ms === null) return '—'
  if (ms < 1000) return `${ms} ms`

  const seconds = Math.round(ms / 1000)
  if (seconds < 60) return `${seconds}s`

  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`

  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`
}

const dateTime = new Intl.DateTimeFormat(LOCALE, {
  year: 'numeric',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

const timeOnly = new Intl.DateTimeFormat(LOCALE, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
})

/** "14 Jun 2026, 08:31" — absolute, for anything that will be quoted or compared. */
export function formatDateTime(iso: string | null): string {
  return iso ? dateTime.format(new Date(iso)) : '—'
}

/** "08:31:07" — for a timeline where the date is already established by context. */
export function formatTime(iso: string): string {
  return timeOnly.format(new Date(iso))
}

/** "3 Feb 2026" — a business date, which has no time of day to show. */
export function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Intl.DateTimeFormat(LOCALE, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    timeZone: UTC,
  }).format(new Date(`${iso}T00:00:00Z`))
}

/** "Feb 2026" — a calendar month, given its first day. */
export function formatMonth(iso: string | null): string {
  if (!iso) return '—'
  return new Intl.DateTimeFormat(LOCALE, { year: 'numeric', month: 'short', timeZone: UTC }).format(
    new Date(`${iso}T00:00:00Z`),
  )
}

const relative = new Intl.RelativeTimeFormat(LOCALE, { numeric: 'auto' })

/**
 * "2 hours ago". Paired with an absolute time in a tooltip, never used alone.
 *
 * Relative time answers "is this current?" at a glance, which is the question the freshness
 * widgets exist to answer. It is a poor answer to "exactly when?", which is why the absolute
 * form is always one hover away.
 */
export function formatRelative(iso: string | null, now: Date = new Date()): string {
  if (!iso) return 'never'

  const seconds = (new Date(iso).getTime() - now.getTime()) / 1000
  const units: Array<[Intl.RelativeTimeFormatUnit, number]> = [
    ['year', 31_536_000],
    ['month', 2_592_000],
    ['day', 86_400],
    ['hour', 3600],
    ['minute', 60],
  ]

  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) return relative.format(Math.round(seconds / size), unit)
  }

  return 'just now'
}
