/**
 * A dashboard definition: what is stored, shared and exported (ADR-016 §4).
 *
 * Configuration only. Results, DOM, component instances, the viewer's access and functions are never
 * part of it - a definition is recomputed against the data and the permissions of whoever opens it.
 * The server checks every definition against `registry/widget-contract.json` before it stores one.
 */

export type CountBy = 'bindings' | 'subscribers' | 'handsets'

/** `all`, `last7` … `last365`, or an explicit `yyyy-MM-dd..yyyy-MM-dd`. */
export type RangeToken = string

/** Lens fields a widget type may honour. */
export type LensFilter = 'vendor' | 'deviceType' | 'operatingSystem' | 'tac' | 'includeUnknownDevice'

/**
 * The view a dashboard is looked at through. Never an MSISDN, IMSI or IMEI: a TAC names a device
 * model, not a person.
 */
export interface DashboardLens {
  countBy?: CountBy
  includeUnknownDevice?: boolean
  range?: RangeToken
  vendor?: string
  deviceType?: string
  operatingSystem?: string
  tac?: string
}

/** A widget's position on the stored 12-column desktop grid. */
export interface WidgetLayout {
  x: number
  y: number
  w: number
  h: number
}

export type WidgetConfig = Record<string, unknown>

/** One widget on a dashboard. */
export interface WidgetInstance<C extends WidgetConfig = WidgetConfig> {
  /** `w_` plus random base-36; stable across saves, new on duplicate-widget and import. */
  id: string
  type: string
  configVersion: number
  /** The widget's own title, or null for the type's name. */
  title: string | null
  subtitle: string | null
  config: C
  /** Lens fields this widget overrides - only those its type supports. */
  overrides: DashboardLens
  layout: WidgetLayout
}

export type RefreshMinutes = 0 | 5 | 15 | 30

export const DEFINITION_SCHEMA_VERSION = 1 as const

export interface DashboardDefinition {
  schemaVersion: typeof DEFINITION_SCHEMA_VERSION
  /** The dashboard's default lens. The URL lens and widget overrides apply on top (ADR-016 §8). */
  defaults: DashboardLens
  /** Auto-refresh for aggregate widgets; 0 is off. Audited widgets never refresh by themselves. */
  refreshMinutes: RefreshMinutes
  widgets: WidgetInstance[]
}

/** A new, random instance id. */
export function newWidgetId(): string {
  const bytes = new Uint8Array(8)
  crypto.getRandomValues(bytes)
  let id = ''
  for (const b of bytes) id += (b % 36).toString(36)
  return `w_${id}`
}

/**
 * A deep copy that shares nothing with the original. Through JSON on purpose: a definition is JSON by
 * contract, and a Vue reactive proxy cannot be structured-cloned.
 */
export function cloneDefinition(definition: DashboardDefinition): DashboardDefinition {
  return JSON.parse(JSON.stringify(definition)) as DashboardDefinition
}

/** The same for one widget's settings, which arrive from a reactive form with every list a proxy. */
export function cloneConfig(config: WidgetConfig): WidgetConfig {
  return JSON.parse(JSON.stringify(config)) as WidgetConfig
}
