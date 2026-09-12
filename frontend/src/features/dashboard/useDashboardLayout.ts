import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'
import { WIDGETS, isKnownWidgetType, type WidgetConfig } from './widgets/registry'

/**
 * Dashboard layout: which widgets are on the board, where, and how they are configured.
 *
 * Persisted to localStorage for now. That is a deliberate v1 scope call, not an
 * oversight: it makes the whole feature work without a users table, and the store's
 * shape is already what a server-side `dashboard` row would hold, so moving it
 * behind an API later is a swap of the load/save functions.
 *
 * The consequence is stated plainly in the UI: a layout is per-browser, not per-user.
 */

export interface WidgetInstance {
  /** Stable id, also the grid item key. */
  i: string
  /** Grid position and size, in 12-column units. */
  x: number
  y: number
  w: number
  h: number
  /** Key into the widget registry. */
  type: string
  config: WidgetConfig
}

const STORAGE_KEY = 'sqm.dashboard.layout.v1'

/** The board a new user sees. Ordered so the summary reads before the detail. */
function defaultLayout(): WidgetInstance[] {
  return [
    { i: 'w-kpi', x: 0, y: 0, w: 12, h: 4, type: 'kpiRow', config: {} },
    {
      i: 'w-class',
      x: 0,
      y: 4,
      w: 12,
      h: 6,
      type: 'series',
      config: { series: 'deviceClass', style: 'composition' },
    },
    {
      i: 'w-vendors',
      x: 0,
      y: 10,
      w: 6,
      h: 11,
      type: 'dimension',
      config: { dimension: 'vendorCanonical', limit: 10, style: 'bar' },
    },
    {
      i: 'w-models',
      x: 6,
      y: 10,
      w: 6,
      h: 11,
      type: 'dimension',
      config: { dimension: 'marketingName', limit: 10, style: 'bar', excludeUnknown: true },
    },
    {
      i: 'w-types',
      x: 0,
      y: 21,
      w: 6,
      h: 11,
      type: 'dimension',
      config: { dimension: 'deviceType', limit: 10, style: 'table' },
    },
    {
      i: 'w-os',
      x: 6,
      y: 21,
      w: 6,
      h: 11,
      type: 'dimension',
      config: { dimension: 'operatingSystem', limit: 8, style: 'table' },
    },
    { i: 'w-quality', x: 0, y: 32, w: 12, h: 7, type: 'quality', config: {} },
  ]
}

/**
 * Validates a stored layout.
 *
 * Storage is user-editable, and a stale layout survives deployments that remove a
 * widget type. Anything unrecognised or malformed is dropped rather than rendered,
 * so a bad entry costs one missing widget instead of a blank dashboard.
 */
function sanitise(raw: unknown): WidgetInstance[] | null {
  if (!Array.isArray(raw)) return null

  const clean = raw.filter((item): item is WidgetInstance => {
    if (typeof item !== 'object' || item === null) return false
    const w = item as Partial<WidgetInstance>
    return (
      typeof w.i === 'string' &&
      typeof w.type === 'string' &&
      isKnownWidgetType(w.type) &&
      typeof w.x === 'number' &&
      typeof w.y === 'number' &&
      typeof w.w === 'number' &&
      typeof w.h === 'number' &&
      w.w > 0 &&
      w.h > 0
    )
  })

  return clean.length > 0 ? clean : null
}

function load(): WidgetInstance[] {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (!stored) return defaultLayout()
    return sanitise(JSON.parse(stored)) ?? defaultLayout()
  } catch {
    // Private windows, blocked storage, or corrupt JSON. A dashboard layout is never
    // worth failing to render over.
    return defaultLayout()
  }
}

export const useDashboardLayout = defineStore('dashboardLayout', () => {
  const widgets = ref<WidgetInstance[]>(load())
  const isEditing = ref(false)

  /** Auto-saves. There is no explicit Save button, so there is nothing to forget. */
  watch(
    widgets,
    (next) => {
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(next))
      } catch {
        // Best effort; the board still works for this session.
      }
    },
    { deep: true },
  )

  const isDefault = computed(
    () => JSON.stringify(widgets.value) === JSON.stringify(defaultLayout()),
  )

  function add(type: string) {
    const def = WIDGETS[type]
    if (!def) return

    // Place below everything currently on the board, so a new widget never lands
    // on top of existing content or scrolls the user somewhere unexpected.
    const bottom = widgets.value.reduce((max, w) => Math.max(max, w.y + w.h), 0)

    widgets.value = [
      ...widgets.value,
      {
        i: `w-${type}-${Date.now().toString(36)}`,
        x: 0,
        y: bottom,
        w: def.defaultSize.w,
        h: def.defaultSize.h,
        type,
        config: { ...def.defaultConfig },
      },
    ]
  }

  function remove(id: string) {
    widgets.value = widgets.value.filter((w) => w.i !== id)
  }

  function updateConfig(id: string, config: WidgetConfig) {
    widgets.value = widgets.value.map((w) => (w.i === id ? { ...w, config } : w))
  }

  function duplicate(id: string) {
    const source = widgets.value.find((w) => w.i === id)
    if (!source) return

    const bottom = widgets.value.reduce((max, w) => Math.max(max, w.y + w.h), 0)
    widgets.value = [
      ...widgets.value,
      { ...source, i: `${source.type}-${Date.now().toString(36)}`, x: 0, y: bottom },
    ]
  }

  function reset() {
    widgets.value = defaultLayout()
  }

  return { widgets, isEditing, isDefault, add, remove, updateConfig, duplicate, reset }
})
