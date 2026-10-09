/**
 * The widget contract, typed. One JSON file is the allow-list for both sides (ADR-016 §3): this file
 * reads it for the registry, and the API embeds it for its validator.
 */
import raw from './widget-contract.json'
import type { LensFilter } from '../types/definition'

export type WidgetGroupId = 'overview' | 'devices' | 'change' | 'risk' | 'quality' | 'operations' | 'tools'

/** current: a current-population figure; series: per-day history; range: a from-to window; fixedWindow: precomputed windows; none: no time axis. */
export type TimeCapability = 'current' | 'series' | 'range' | 'fixedWindow' | 'none'

/** auto: loads on open; explicit: runs on an explicit Run only (audited); none: fetches nothing. */
export type Execution = 'auto' | 'explicit' | 'none'

export type FieldKind =
  | 'enum'
  | 'enumList'
  | 'int'
  | 'bool'
  | 'string'
  | 'stringList'
  | 'date'
  | 'range'
  | 'explorerQuery'

export interface ConfigFieldSpec {
  kind: FieldKind
  required?: boolean
  nullable?: boolean
  default: unknown
  values?: string[]
  min?: number
  max?: number
  maxLength?: number
  pattern?: string
  minItems?: number
  maxItems?: number
}

export interface WidgetSizeSpec {
  w: number
  h: number
  minW: number
  minH: number
}

export interface WidgetTypeSpec {
  configVersion: number
  group: WidgetGroupId
  permissions: string[]
  permissionsByConfig?: { field: string; map: Record<string, string[]> }
  filters: LensFilter[]
  measures: string[]
  time: TimeCapability
  execution: Execution
  size: WidgetSizeSpec
  config: Record<string, ConfigFieldSpec>
}

export interface WidgetContract {
  contractVersion: number
  limits: {
    titleMaxLength: number
    subtitleMaxLength: number
    gridColumns: number
    maxRows: number
    maxWidgetHeight: number
  }
  lens: {
    countBy: string[]
    ranges: string[]
    rangePattern: string
    textFilterMaxLength: number
    tacPattern: string
    refreshMinutes: number[]
  }
  groups: { id: WidgetGroupId; name: string }[]
  types: Record<string, WidgetTypeSpec>
}

export const CONTRACT = raw as unknown as WidgetContract

export function typeSpec(type: string): WidgetTypeSpec | undefined {
  return Object.hasOwn(CONTRACT.types, type) ? CONTRACT.types[type] : undefined
}

/** A config with every field of the type present, defaults filled. Unknown fields are dropped. */
export function defaultConfig(type: string): Record<string, unknown> {
  const spec = typeSpec(type)
  if (!spec) return {}
  const config: Record<string, unknown> = {}
  for (const [name, field] of Object.entries(spec.config)) {
    config[name] = structuredClone(field.default)
  }
  return config
}

/** The permissions one instance needs: the type's own, plus those its config field selects. */
export function requiredPermissions(type: string, config: Record<string, unknown>): string[] {
  const spec = typeSpec(type)
  if (!spec) return []
  const required = [...spec.permissions]
  const byConfig = spec.permissionsByConfig
  if (byConfig) {
    const value = config[byConfig.field]
    if (typeof value === 'string' && Object.hasOwn(byConfig.map, value)) {
      required.push(...(byConfig.map[value] ?? []))
    }
  }
  return [...new Set(required)]
}
