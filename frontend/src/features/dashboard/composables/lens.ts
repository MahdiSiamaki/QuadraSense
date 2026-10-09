/**
 * The lens a widget is computed with (ADR-016 §8). The same rules as
 * `Sqm.Application.Dashboards.DashboardLensResolver`; a test on each side pins them.
 *
 * Precedence: the dashboard's defaults, then the URL lens, then the widget's own override - and only
 * for fields the widget's type supports. A field it does not support is undefined in the result, so a
 * network-wide card is never drawn as if a vendor filter applied to it.
 *
 * Relative ranges count back from the latest day in the data, not from today: files arrive weeks
 * late, and "the last 30 days" of a feed that ends a month ago would otherwise be empty.
 */
import { CONTRACT, type WidgetTypeSpec } from '../registry/contract'
import type { CountBy, DashboardLens, LensFilter } from '../types/definition'

export const DEFAULT_COUNT_BY: CountBy = 'bindings'
export const DEFAULT_RANGE = 'all'

const TEXT_FILTERS = ['vendor', 'deviceType', 'operatingSystem'] as const

function supports(spec: WidgetTypeSpec, filter: LensFilter): boolean {
  return spec.filters.includes(filter)
}

function first(...values: (string | undefined)[]): string | undefined {
  return values.find((v) => v !== undefined && v.trim() !== '')
}

export function supportsRange(spec: WidgetTypeSpec): boolean {
  return spec.time === 'series' || spec.time === 'range'
}

export function supportsCountBy(spec: WidgetTypeSpec): boolean {
  return spec.measures.length > 0
}

/** The effective lens for one widget. */
export function effectiveLens(
  spec: WidgetTypeSpec,
  defaults: DashboardLens,
  url: DashboardLens | undefined,
  overrides: DashboardLens,
): DashboardLens {
  const u = url ?? {}
  const result: DashboardLens = {}

  if (supportsCountBy(spec)) {
    let countBy = (first(overrides.countBy, u.countBy, defaults.countBy) ?? DEFAULT_COUNT_BY) as CountBy
    if (!spec.measures.includes(countBy)) countBy = spec.measures[0] as CountBy
    result.countBy = countBy
  }
  if (supports(spec, 'includeUnknownDevice')) {
    result.includeUnknownDevice =
      overrides.includeUnknownDevice ?? u.includeUnknownDevice ?? defaults.includeUnknownDevice ?? true
  }
  if (supportsRange(spec)) {
    result.range = first(overrides.range, u.range, defaults.range) ?? DEFAULT_RANGE
  }
  for (const key of TEXT_FILTERS) {
    if (supports(spec, key)) {
      const value = first(overrides[key], u[key], defaults[key])
      if (value !== undefined) result[key] = value
    }
  }
  if (supports(spec, 'tac')) {
    const tac = first(overrides.tac, u.tac, defaults.tac)
    if (tac !== undefined) result.tac = tac
  }
  return result
}

/** Whether a token is a relative range, and how many days it covers. */
export function relativeDays(range: string): number | null {
  const m = /^last(\d+)$/.exec(range)
  if (!m) return null
  const days = Number(m[1])
  return days > 0 ? days : null
}

/** An explicit range's bounds, or null. */
export function explicitRange(range: string): { from: string; to: string } | null {
  const m = /^(\d{4}-\d{2}-\d{2})\.\.(\d{4}-\d{2}-\d{2})$/.exec(range)
  if (!m) return null
  const from = m[1]!
  const to = m[2]!
  if (!isIsoDate(from) || !isIsoDate(to) || from > to) return null
  return { from, to }
}

function isIsoDate(value: string): boolean {
  const d = new Date(`${value}T00:00:00Z`)
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value
}

/** Adds days to a business date, in UTC so no timezone moves it. */
export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}

/**
 * The days a range covers, given the latest day in the data. Null bounds are open: `all` is
 * `{from: null, to: null}`, and a relative range stays open until the data's last day is known.
 */
export function resolveRange(
  range: string | undefined,
  dataThrough: string | null,
): { from: string | null; to: string | null } {
  if (!range || range === 'all') return { from: null, to: null }
  const days = relativeDays(range)
  if (days !== null) {
    return dataThrough ? { from: addDays(dataThrough, -(days - 1)), to: dataThrough } : { from: null, to: null }
  }
  const explicit = explicitRange(range)
  return explicit ?? { from: null, to: null }
}

/** Whether a range token is one the contract allows. */
export function isValidRange(range: string | undefined): boolean {
  if (range === undefined) return false
  return CONTRACT.lens.ranges.includes(range) || explicitRange(range) !== null
}

/**
 * The URL lens with every value the contract would refuse dropped: a hand-edited link degrades to
 * "no filter" instead of failing - the rule `useFilterState` has always followed.
 */
export function sanitizeLens(lens: DashboardLens): DashboardLens {
  const result: DashboardLens = {}
  if (lens.countBy && CONTRACT.lens.countBy.includes(lens.countBy)) result.countBy = lens.countBy
  if (typeof lens.includeUnknownDevice === 'boolean') result.includeUnknownDevice = lens.includeUnknownDevice
  if (isValidRange(lens.range)) result.range = lens.range
  for (const key of TEXT_FILTERS) {
    const value = lens[key]?.trim()
    if (value && value.length <= CONTRACT.lens.textFilterMaxLength) result[key] = value
  }
  if (lens.tac && new RegExp(CONTRACT.lens.tacPattern).test(lens.tac)) result.tac = lens.tac
  return result
}

/** Human labels for range tokens. */
export function rangeLabel(range: string | undefined): string {
  switch (range) {
    case undefined:
    case 'all':
      return 'All available days'
    case 'last7':
      return 'Last 7 days of data'
    case 'last30':
      return 'Last 30 days of data'
    case 'last90':
      return 'Last 90 days of data'
    case 'last180':
      return 'Last 180 days of data'
    case 'last365':
      return 'Last 365 days of data'
    default: {
      const explicit = explicitRange(range)
      return explicit ? `${explicit.from} to ${explicit.to}` : 'All available days'
    }
  }
}
