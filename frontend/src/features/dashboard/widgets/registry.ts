import type { Component } from 'vue'
import { defineAsyncComponent } from 'vue'
import type { Dimension } from '@/api/dashboard'

/**
 * The widget catalogue.
 *
 * Everything the dashboard can show is declared here, once. A saved layout stores
 * only a widget `type` plus its config, so adding a widget to the product means
 * adding an entry to this file — not touching the grid, the store, or the page.
 *
 * The `type` strings are a closed set and are validated when a layout is loaded,
 * because saved layouts live in browser storage and are therefore user-editable.
 * An unknown type is dropped rather than rendered.
 */

export type SeriesName =
  | 'deviceClass'
  | 'devicesPerSubscriber'
  | 'subscribersPerDevice'
  | 'msisdnPrefix'

export type ChartStyle = 'bar' | 'table' | 'composition'

/** Per-widget settings a user can change from the UI. */
export interface WidgetConfig {
  /** For dimension widgets: what to group by. */
  dimension?: Dimension
  /** For series widgets: which pre-computed series to read. */
  series?: SeriesName
  /** How many rows to show. */
  limit?: number
  /** How to draw it. Several widgets support more than one form. */
  style?: ChartStyle
  /** Override the default title. */
  title?: string
  /** Exclude the 000000 bucket from this widget specifically. */
  excludeUnknown?: boolean
}

export interface WidgetDefinition {
  type: string
  /** Shown in the "add widget" gallery. */
  name: string
  /** One line explaining what question it answers. */
  description: string
  /** Grouping in the gallery. */
  category: 'Overview' | 'Devices' | 'Subscribers' | 'Quality'
  component: Component
  defaultConfig: WidgetConfig
  /** Default grid size in columns/rows. The grid is 12 columns wide. */
  defaultSize: { w: number; h: number }
  /** Which config fields this widget exposes for editing. */
  editable: Array<keyof WidgetConfig>
}

const KpiRowWidget = defineAsyncComponent(() => import('./KpiRowWidget.vue'))
const DimensionWidget = defineAsyncComponent(() => import('./DimensionWidget.vue'))
const SeriesWidget = defineAsyncComponent(() => import('./SeriesWidget.vue'))
const QualityWidget = defineAsyncComponent(() => import('./QualityWidget.vue'))

export const WIDGETS: Record<string, WidgetDefinition> = {
  kpiRow: {
    type: 'kpiRow',
    name: 'Headline figures',
    description: 'Active bindings, subscribers, devices, unknown devices and TAC coverage.',
    category: 'Overview',
    component: KpiRowWidget,
    defaultConfig: {},
    defaultSize: { w: 12, h: 4 },
    editable: ['title'],
  },

  dimension: {
    type: 'dimension',
    name: 'Breakdown by dimension',
    description: 'Rank vendors, models, device types or operating systems by binding count.',
    category: 'Devices',
    component: DimensionWidget,
    defaultConfig: { dimension: 'vendorCanonical', limit: 10, style: 'bar' },
    defaultSize: { w: 6, h: 11 },
    editable: ['dimension', 'limit', 'style', 'title', 'excludeUnknown'],
  },

  series: {
    type: 'series',
    name: 'Pre-computed series',
    description:
      'Device class mix, devices per subscriber, subscribers per device, or number prefixes.',
    category: 'Subscribers',
    component: SeriesWidget,
    defaultConfig: { series: 'deviceClass', style: 'composition' },
    defaultSize: { w: 12, h: 6 },
    editable: ['series', 'style', 'title'],
  },

  quality: {
    type: 'quality',
    name: 'Enrichment breakdown',
    description: 'How every active binding is accounted for, and which part is a real defect.',
    category: 'Quality',
    component: QualityWidget,
    defaultConfig: {},
    defaultSize: { w: 12, h: 7 },
    editable: ['title'],
  },
}

export const WIDGET_LIST = Object.values(WIDGETS)

/** Human labels for the dimension picker. */
export const DIMENSION_OPTIONS: Array<{ value: Dimension; label: string }> = [
  { value: 'vendorCanonical', label: 'Vendor (normalised)' },
  { value: 'manufacturer', label: 'Manufacturer (raw GSMA)' },
  { value: 'marketingName', label: 'Model' },
  { value: 'deviceType', label: 'Device type' },
  { value: 'operatingSystem', label: 'Operating system' },
  { value: 'tac', label: 'TAC' },
]

export const SERIES_OPTIONS: Array<{ value: SeriesName; label: string }> = [
  { value: 'deviceClass', label: 'Device class mix' },
  { value: 'devicesPerSubscriber', label: 'Devices per subscriber' },
  { value: 'subscribersPerDevice', label: 'Subscribers per device (dual-SIM)' },
  { value: 'msisdnPrefix', label: 'Subscriber number prefix' },
]

export function isKnownWidgetType(type: string): boolean {
  return Object.hasOwn(WIDGETS, type)
}
