/**
 * The widget registry's types (ADR-016 §3-§4).
 *
 * A widget **definition** is code: what a type is, how it looks in the catalogue, which component
 * draws it and which form configures it. A widget **instance** is data (`WidgetInstance`): a type,
 * a config and a position. One definition, any number of instances with different configs.
 *
 * What a type is *allowed* to be configured with - fields, bounds, permissions, filters, time - comes
 * from `registry/widget-contract.json`, which the server also enforces; the registry attaches it as
 * `spec` and a test fails if the two disagree.
 */
import type { Component, InjectionKey, Ref } from 'vue'
import type { WidgetGroupId, WidgetTypeSpec } from '../registry/contract'
import type { DashboardLens, WidgetConfig, WidgetInstance } from './definition'

/** Icons the catalogue and card headers may use; drawn by `components/WidgetIcon.vue`. */
export type WidgetIconName =
  | 'kpi'
  | 'trend'
  | 'bar'
  | 'table'
  | 'composition'
  | 'capability'
  | 'calendar'
  | 'movement'
  | 'compare'
  | 'shield'
  | 'list'
  | 'quality'
  | 'clock'
  | 'upload'
  | 'server'
  | 'tag'
  | 'search'
  | 'query'
  | 'device'

/** A catalogue entry that is an existing type with a starting config: "Top models" is `device.top`. */
export interface WidgetPreset<C extends WidgetConfig = WidgetConfig> {
  /** Unique within its type. */
  key: string
  name: string
  description: string
  /** The catalogue group, when it differs from the type's ("Unknown device" is a KPI shown under Data Quality). */
  group?: WidgetGroupId
  tags?: string[]
  aliases?: string[]
  config: Partial<C>
  /** A title for the new instance; the preset's name when omitted. */
  title?: string
}

/** A field-level problem, for forms and the save refusal. */
export interface FieldError {
  field: string
  message: string
}

/** Where a field sits in the configure form. Advanced appears only when a type has such fields. */
export type FieldSection = 'general' | 'data' | 'display' | 'advanced'

/**
 * How the generic configure form (`components/ConfigForm.vue`) presents one contract field. The
 * field's kind, bounds and allowed values come from the contract; this is only the words and order.
 */
export interface FieldPresentation<C extends WidgetConfig = WidgetConfig> {
  label: string
  help?: string
  section: FieldSection
  /** Labels for enum / enumList values, in the order they should be offered. */
  options?: Record<string, string>
  /** Hide the field unless the rest of the config makes it meaningful. */
  visibleWhen?: (config: C) => boolean
  /** Whether changing it only redraws (true) or needs new data (false, the default). Drives the preview debounce. */
  visual?: boolean
}

/** What the catalogue and the configure step say about a type. */
export interface WidgetDefinition<C extends WidgetConfig = WidgetConfig> {
  type: string
  name: string
  description: string
  /** Search terms beyond name and description: "SIM swap" finds the SIM-change widgets. */
  tags: string[]
  aliases: string[]
  icon: WidgetIconName
  /** The count unit in words, e.g. "Bindings, or estimated subscribers / handsets". */
  unit: string
  /** Where the data comes from, in words a person can check: "GET /api/v1/dashboard/kpi". */
  source: string
  /** The data's time scope in words: "Current population, as of the latest refreshed delivery". */
  timeScope: string
  /** Caveats every instance carries: estimated counts, network-wide, feed defects. */
  caveats: string[]
  presets?: WidgetPreset<C>[]
  /** Draws an instance. Receives `WidgetViewProps`. */
  component: Component
  /**
   * How each contract field is presented by the generic form. Every config field of the type must
   * appear here or in `customFields` (a test checks).
   */
  fields: Partial<Record<keyof C & string, FieldPresentation<C>>>
  /**
   * A form for fields the generic form cannot draw (an Explorer query, a pair of baseline dates), shown
   * in the Data & filters section. Receives `WidgetSettingsProps`; emits `update:config` with a whole
   * new config.
   */
  settings?: Component
  /** Fields `settings` draws, so the generic form leaves them out. */
  customFields?: (keyof C & string)[]
  /** Semantic checks beyond the contract's bounds (e.g. a custom baseline needs both dates). */
  validate?: (config: C) => FieldError[]
  /** What exports can take from it. */
  exports: { csv: boolean; image: boolean }
  /**
   * `card` (default): the frame draws the card, its title and subtitle, and the widget draws the body.
   * `bare`: the widget is itself a card (a KPI is a `KpiCard`), so the frame draws only the edit
   * controls around it.
   */
  chrome?: 'card' | 'bare'
}

/** A definition with its contract attached by the registry. */
export interface ResolvedWidgetDefinition<C extends WidgetConfig = WidgetConfig> extends WidgetDefinition<C> {
  spec: WidgetTypeSpec
  group: WidgetGroupId
}

/** Where an instance is being drawn. */
export type WidgetMode = 'view' | 'edit' | 'preview' | 'export'

/**
 * The lens a widget is computed with: only the fields its type supports are set, the rest are
 * undefined (`composables/lens.ts`).
 */
export type EffectiveLens = DashboardLens

/** One widget's server-prepared data, for exports (mirrors `WidgetSnapshot` in Sqm.Contracts). */
export interface WidgetSnapshotData {
  id: string
  type: string
  status: 'ok' | 'forbidden' | 'notReady' | 'error' | 'interactive' | 'noData'
  reason: string | null
  data: unknown
  asOf: string | null
  notes: string[]
  effective: DashboardLens
}

/** Props every widget component receives. */
export interface WidgetViewProps<C extends WidgetConfig = WidgetConfig> {
  instance: WidgetInstance<C>
  lens: EffectiveLens
  mode: WidgetMode
  /** In export mode, the server-prepared data to draw instead of querying. */
  snapshot?: WidgetSnapshotData | null
  /** False until the card is in view (or always true in preview and export). Queries wait for it. */
  active: boolean
}

/** Props every settings form receives; it emits `update:config` with a whole new config. */
export interface WidgetSettingsProps<C extends WidgetConfig = WidgetConfig> {
  config: C
  instance: WidgetInstance<C>
  /** Lens fields the type supports, so a form can offer overrides only for those. */
  spec: WidgetTypeSpec
}

/** What the dashboard page provides to every widget. */
export interface DashboardContext {
  /** view, edit, export. */
  mode: Ref<WidgetMode>
  /** Drill-down from a chart: sets a URL lens filter, undoable with Back. */
  setLensFilter: (key: 'vendor' | 'deviceType' | 'operatingSystem' | 'tac', value: string | undefined) => void
  /** The latest day in the change log, for relative ranges and footers; null until known. */
  dataThrough: Ref<string | null>
  /** Bumped by the toolbar's Refresh; aggregate widgets refetch on change, audited ones do not. */
  refreshToken: Ref<number>
  /** In export mode, the server-prepared data by widget id. */
  snapshot: Ref<Map<string, WidgetSnapshotData> | null>
}

export const DASHBOARD_CONTEXT: InjectionKey<DashboardContext> = Symbol('dashboard-context')

/**
 * Types a definition against its own config. Each group exports its definitions from
 * `widgets/<group>/index.ts` as `export const definitions = [defineWidget({...}), ...]`; the registry
 * collects every group with `import.meta.glob`.
 */
export function defineWidget<C extends WidgetConfig>(definition: WidgetDefinition<C>): WidgetDefinition<C> {
  return definition
}
