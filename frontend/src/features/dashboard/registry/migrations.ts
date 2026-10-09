/**
 * Reading a stored or imported definition into this build's shape (ADR-016 §4).
 *
 * Deterministic and pure: the same input always gives the same output, so a definition upgraded in
 * two browsers is the same definition. Two kinds of change:
 *
 *  - absent optional parts are filled (defaults, refresh, overrides, title, subtitle, config fields
 *    with their contract defaults) - the shape every other module can rely on;
 *  - per-type config upgrades, keyed by type and the version they upgrade from, applied in order.
 *    There are none yet; the table exists so the first one is an entry, not a design.
 *
 * What is never done here: dropping a widget. An unknown, retired or newer widget is kept as it was,
 * listed in `problems`, and drawn by the page as a fallback card the person can remove - silently
 * losing part of somebody's dashboard on open would be worse than showing it cannot be drawn.
 */
import { DEFINITION_SCHEMA_VERSION, type DashboardDefinition, type WidgetConfig, type WidgetInstance } from '../types/definition'
import { typeSpec } from './contract'
import { normalizeConfig } from './validate'

/** Upgrades one type's config from `from` to `from + 1`. Must be pure. */
export type ConfigMigration = (config: WidgetConfig) => WidgetConfig

/** type -> from-version -> migration. */
export type ConfigMigrations = Record<string, Record<number, ConfigMigration>>

/** The real table. Empty while every type is at version 1. */
export const CONFIG_MIGRATIONS: ConfigMigrations = {}

export interface MigrationResult {
  definition: DashboardDefinition
  /** Whether anything was filled in or upgraded - the dashboard should be saved again to keep it. */
  changed: boolean
  /** What could not be brought up to date, in words. */
  problems: string[]
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function migrateDefinition(raw: unknown, migrations: ConfigMigrations = CONFIG_MIGRATIONS): MigrationResult {
  const problems: string[] = []
  if (!isObject(raw)) {
    return {
      definition: { schemaVersion: DEFINITION_SCHEMA_VERSION, defaults: {}, refreshMinutes: 0, widgets: [] },
      changed: true,
      problems: ['The stored dashboard is not a definition; it opens empty.'],
    }
  }

  const before = JSON.stringify(raw)

  if (typeof raw.schemaVersion === 'number' && raw.schemaVersion > DEFINITION_SCHEMA_VERSION) {
    // A newer build wrote it. Reading it as ours would guess; keep it whole and say so.
    return {
      definition: raw as unknown as DashboardDefinition,
      changed: false,
      problems: ['This dashboard was saved by a newer version of QuadraSense.'],
    }
  }

  const widgets: WidgetInstance[] = (Array.isArray(raw.widgets) ? raw.widgets : []).filter(isObject).map((w) => {
    const type = String(w.type ?? '')
    let config: WidgetConfig = isObject(w.config) ? { ...w.config } : {}
    let configVersion = typeof w.configVersion === 'number' ? w.configVersion : 1
    const spec = typeSpec(type)

    if (spec) {
      const steps = migrations[type] ?? {}
      while (configVersion < spec.configVersion && steps[configVersion]) {
        config = steps[configVersion]!(config)
        configVersion += 1
      }
      if (configVersion === spec.configVersion) config = normalizeConfig(type, config)
      else if (configVersion > spec.configVersion) problems.push(`${type} was saved by a newer version.`)
      else problems.push(`${type} settings version ${configVersion} cannot be upgraded.`)
    } else {
      problems.push(`${type || 'A widget'} is not available in this version.`)
    }

    const layout = isObject(w.layout) ? w.layout : {}
    return {
      id: String(w.id ?? ''),
      type,
      configVersion,
      title: typeof w.title === 'string' ? w.title : null,
      subtitle: typeof w.subtitle === 'string' ? w.subtitle : null,
      config,
      overrides: isObject(w.overrides) ? { ...w.overrides } : {},
      layout: {
        x: Number(layout.x ?? 0),
        y: Number(layout.y ?? 0),
        w: Number(layout.w ?? spec?.size.w ?? 4),
        h: Number(layout.h ?? spec?.size.h ?? 4),
      },
    }
  })

  const definition: DashboardDefinition = {
    schemaVersion: DEFINITION_SCHEMA_VERSION,
    defaults: isObject(raw.defaults) ? { ...raw.defaults } : {},
    refreshMinutes: ([0, 5, 15, 30] as const).includes(raw.refreshMinutes as 0) ? (raw.refreshMinutes as 0 | 5 | 15 | 30) : 0,
    widgets,
  }

  return { definition, changed: JSON.stringify(definition) !== before, problems }
}
