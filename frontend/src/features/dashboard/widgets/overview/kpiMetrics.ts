/**
 * The figures a KPI widget can show, each with the words that make it readable.
 *
 * The first six are the dashboard page's KPI row, and their identifier, qualifier and definition are
 * the page's own text, unchanged: each one was written after a measurement showed the figure being
 * misread. The other three are the page's Enrichment breakdown, as cards.
 */
import type { KpiSummary } from '@/api/dashboard'
import { formatPercent } from '@/lib/format'

export const KPI_METRICS = [
  'activeBindings',
  'distinctSubscribers',
  'distinctSims',
  'distinctDevices',
  'unknownDeviceBindings',
  'malformedImeiBindings',
  'tacMatchedBindings',
  'unregisteredTacBindings',
  'tacCoveragePercent',
] as const

export type KpiMetric = (typeof KPI_METRICS)[number]

export type KpiNumberFormat = 'compact' | 'full'

export interface KpiMetricSpec {
  /** The card's label. */
  label: string
  /** The metric in the configure form and the catalogue: unit named, estimates said. */
  option: string
  /** What it counts, in monospace under the figure. */
  identifier: string
  /** A fixed qualifier, or one computed from the summary ("7.6% of bindings"). */
  qualifier: string | ((kpi: KpiSummary, precision: number) => string | undefined) | undefined
  definition: string
  percent: boolean
  tone: 'default' | 'warning'
  value: (kpi: KpiSummary) => number
}

/**
 * Bindings with a well-formed IMEI whose TAC is absent from the GSMA database.
 *
 * Computed from exact counts the API returns, never from the coverage percentage.
 * Deriving it from a figure rounded to three decimals put this ~30,000 rows off
 * the true 255,676 - a rounding artifact presented as a count, on the screen
 * whose whole job is to report data quality accurately.
 */
export function unregisteredTacBindings(kpi: KpiSummary): number {
  return kpi.activeBindings - kpi.tacMatchedBindings - kpi.unknownDeviceBindings - kpi.malformedImeiBindings
}

/** "7.6% of bindings", from exact counts; nothing when there are no bindings to be a share of. */
export function shareOfBindings(count: number, kpi: KpiSummary, precision: number): string | undefined {
  if (!kpi.activeBindings) return undefined
  return `${formatPercent((count / kpi.activeBindings) * 100, precision)} of bindings`
}

export const KPI_METRIC_SPECS: Record<KpiMetric, KpiMetricSpec> = {
  activeBindings: {
    label: 'Active bindings',
    option: 'Active bindings',
    identifier: 'MSISDN + IMSI + IMEI',
    qualifier: undefined,
    definition:
      'Rows in the current state: one per distinct number-SIM-handset combination. This is the grain of the whole dataset, not a count of people.',
    percent: false,
    tone: 'default',
    value: (k) => k.activeBindings,
  },
  /*
    Same accumulation as Handsets, smaller but not small: a number stays counted until the
    source removes it. Measured by how recently the feed last said anything about each
    number - 27.7% have NEVER appeared in a daily file, and only 36.1% were confirmed in the
    last 30 days. "still counted" is the honest short form.
  */
  distinctSubscribers: {
    label: 'Subscribers',
    option: 'Subscribers (estimated)',
    identifier: 'MSISDN',
    qualifier: 'approx. · incl. dormant',
    definition:
      'Distinct phone numbers with at least one active binding - not a count of numbers in use today. A binding stays active until the source removes it, so a number that has gone quiet is still counted. Measured by when the feed last mentioned each number: 27.7% have never appeared in a daily file at all, coming from the initial dump; 36.1% were confirmed in the last 30 days, 22.3% within 90, and 13.9% not for over 90 days. Estimated with HyperLogLog (~0.5% error); exact counts are available via export.',
    percent: false,
    tone: 'default',
    value: (k) => k.distinctSubscribers,
  },
  distinctSims: {
    label: 'SIM cards',
    option: 'SIM cards (estimated)',
    identifier: 'IMSI',
    qualifier: 'approx. · incl. dormant',
    definition:
      'Distinct SIM identities with an active binding - not a count of SIMs in use today, for the same reason as Subscribers: 28.2% have never appeared in a daily file, 35.8% were confirmed in the last 30 days. Slightly higher than the number of subscribers because a number swapped to a new SIM has more than one IMSI - measured at 1.0017 SIMs per number, with 97.8% of numbers holding exactly one.',
    percent: false,
    tone: 'default',
    value: (k) => k.distinctSims,
  },
  /*
    The qualifier says "includes replaced" because this figure is not what its label
    suggests, and the difference is large enough to change a conclusion.

    It counts IMEIs holding an ACTIVE binding, and a binding stays active until the source
    removes it - so a phone somebody replaced is still in here. Measured: 1.079 handsets per
    SIM across the whole population, against 0.996 for bindings the feed confirmed within
    the last 30 days, where the ratio is the 1:1 it should be. The gap is accumulated
    history, not devices.

    Stated on the face of the card rather than only in the tooltip: a reader who never
    hovers would otherwise read it as "phones on the network today", which is the exact
    misreading that prompted this.
  */
  distinctDevices: {
    label: 'Handsets',
    option: 'Handsets (estimated)',
    identifier: 'IMEI',
    qualifier: 'approx. · includes replaced',
    definition:
      'Distinct 14-digit IMEIs holding an active binding - NOT a count of handsets in use today. A binding stays active until the source removes it, so a phone that has been replaced is still counted. Measured: 1.079 handsets per SIM overall, against 0.996 for bindings confirmed in the last 30 days, where the ratio is 1:1 as expected; 45% of active bindings have never appeared in a daily file at all, coming from the initial dump, which covers a 30-day window rather than an instant. A dual-SIM phone does not lower this: the GSMA record gives 77% of bindings two IMEIs, one per radio. Excludes the 000000 sentinel, which is one literal value and not a handset.',
    percent: false,
    tone: 'default',
    value: (k) => k.distinctDevices,
  },
  unknownDeviceBindings: {
    label: 'Unknown device',
    option: 'Unknown device (bindings)',
    identifier: 'IMEI = 000000',
    qualifier: (k, precision) => shareOfBindings(k.unknownDeviceBindings, k, precision),
    definition:
      'Bindings where the source reports the handset as unknown. A sentinel value, not a data defect - these are real subscribers whose device the operator did not record.',
    percent: false,
    tone: 'warning',
    value: (k) => k.unknownDeviceBindings,
  },
  malformedImeiBindings: {
    label: 'Malformed IMEI',
    option: 'Malformed IMEI (bindings)',
    identifier: 'IMEI ≠ 14 digits',
    qualifier: (k, precision) => shareOfBindings(k.malformedImeiBindings, k, precision),
    definition:
      'Active bindings whose IMEI is numeric but not 14 digits. The only genuine defect among the enrichment categories - worth raising with the source. Not the same as Unknown device: 000000 is a value the source uses on purpose for a handset it did not record, while a malformed IMEI should identify a handset and does not, so it is not counted as a handset and its model cannot be known.',
    percent: false,
    tone: 'warning',
    value: (k) => k.malformedImeiBindings,
  },
  tacMatchedBindings: {
    label: 'Enriched from GSMA',
    option: 'Enriched from GSMA (bindings)',
    identifier: 'TAC',
    qualifier: (k, precision) => shareOfBindings(k.tacMatchedBindings, k, precision),
    definition:
      'Active bindings whose TAC (the first 8 digits of the IMEI) matched the GSMA device database, so manufacturer and model are known. An exact count: TAC coverage is the same figure as a share of active bindings. The ceiling on it is set by unknown-device rows, not by gaps in the GSMA database.',
    percent: false,
    tone: 'default',
    value: (k) => k.tacMatchedBindings,
  },
  unregisteredTacBindings: {
    label: 'Unregistered TAC',
    option: 'Unregistered TAC (bindings)',
    identifier: 'TAC',
    qualifier: (k, precision) => shareOfBindings(unregisteredTacBindings(k), k, precision),
    definition:
      'Active bindings with a well-formed 14-digit IMEI whose TAC is absent from the GSMA database, so the model cannot be named. Counted exactly: active bindings minus those enriched from GSMA, unknown-device and malformed-IMEI bindings - never derived from the coverage percentage, which is rounded. A characteristic of the feed and of the GSMA export, not a defect in either.',
    percent: false,
    tone: 'default',
    value: unregisteredTacBindings,
  },
  tacCoveragePercent: {
    label: 'TAC coverage',
    option: 'TAC coverage (% of bindings)',
    identifier: 'TAC',
    qualifier: 'of bindings',
    definition:
      'Share of active bindings whose TAC (the first 8 digits of the IMEI) matched the GSMA device database, so manufacturer and model are known.',
    percent: true,
    tone: 'default',
    value: (k) => k.tacCoveragePercent,
  },
}

/** A metric's spec; an unknown value falls back to active bindings rather than drawing nothing. */
export function metricSpec(metric: string): KpiMetricSpec {
  return (KPI_METRIC_SPECS as Record<string, KpiMetricSpec | undefined>)[metric] ?? KPI_METRIC_SPECS.activeBindings
}

export function metricQualifier(spec: KpiMetricSpec, kpi: KpiSummary | undefined, precision: number): string | undefined {
  if (typeof spec.qualifier === 'function') return kpi ? spec.qualifier(kpi, precision) : undefined
  return spec.qualifier
}

/** The configure form's option labels for the metric enum, in contract order. */
export const KPI_METRIC_OPTIONS: Record<string, string> = Object.fromEntries(
  KPI_METRICS.map((m) => [m, KPI_METRIC_SPECS[m].option]),
)
