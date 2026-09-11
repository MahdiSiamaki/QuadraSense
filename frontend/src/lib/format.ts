/**
 * Number and identifier formatting.
 *
 * Locale is fixed to en-US rather than the browser's: this is an English-only,
 * Gregorian-only product by decision, and a user whose OS is set to a locale
 * with different grouping separators should still see the same figure as the
 * colleague they are discussing it with.
 */

const LOCALE = 'en-US'

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

export function formatPercent(value: number, digits = 1): string {
  return `${value.toFixed(digits)}%`
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
    return new Intl.DateTimeFormat(LOCALE, { month: 'short', day: 'numeric' }).format(
      new Date(dataDate),
    )
  }
  return `#${sequence}`
}
