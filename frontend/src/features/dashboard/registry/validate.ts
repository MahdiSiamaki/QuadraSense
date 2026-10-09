/**
 * The contract's rules in the browser, so a form can say what is wrong before anything is sent and a
 * save can be refused before it reaches the server. The server's `DashboardDefinitionValidator` is
 * authoritative; this mirrors it closely enough that it is never the one to say no first.
 */
import type { DashboardDefinition, DashboardLens, WidgetConfig, WidgetInstance } from '../types/definition'
import type { FieldError } from '../types/widget'
import { CONTRACT, typeSpec, type ConfigFieldSpec } from './contract'
import { isValidRange, supportsCountBy, supportsRange } from '../composables/lens'

export const INSTANCE_ID = /^w_[a-z0-9]{6,32}$/

const LENS_KEYS = ['countBy', 'includeUnknownDevice', 'range', 'vendor', 'deviceType', 'operatingSystem', 'tac'] as const
const IDENTIFIER_FIELDS = new Set(['msisdn', 'imsi', 'imei'])

function isIsoDate(value: unknown): boolean {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const d = new Date(`${value}T00:00:00Z`)
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value
}

/** Every field of the type, defaults filled; unknown fields dropped. */
export function normalizeConfig(type: string, config: WidgetConfig): WidgetConfig {
  const spec = typeSpec(type)
  if (!spec) return { ...config }
  const result: WidgetConfig = {}
  for (const [name, field] of Object.entries(spec.config)) {
    result[name] = Object.hasOwn(config, name) ? config[name] : structuredClone(field.default)
  }
  return result
}

function fieldError(name: string, value: unknown, field: ConfigFieldSpec): string | null {
  if (value === null || value === undefined) {
    if (field.required) return 'Required.'
    return field.nullable || value === undefined ? null : 'A value is needed here.'
  }

  switch (field.kind) {
    case 'enum':
      return typeof value === 'string' && field.values!.includes(value) ? null : 'Not one of the allowed values.'
    case 'enumList': {
      if (!Array.isArray(value)) return 'A list.'
      if (value.some((v) => typeof v !== 'string' || !field.values!.includes(v))) return 'An item is not allowed.'
      if (new Set(value).size !== value.length) return 'An item appears twice.'
      if (field.minItems !== undefined && value.length < field.minItems) return `Choose at least ${field.minItems}.`
      if (field.maxItems !== undefined && value.length > field.maxItems) return `Choose at most ${field.maxItems}.`
      return null
    }
    case 'int': {
      if (typeof value !== 'number' || !Number.isInteger(value)) return 'A whole number.'
      if (field.min !== undefined && value < field.min) return `At least ${field.min}.`
      if (field.max !== undefined && value > field.max) return `At most ${field.max}.`
      return null
    }
    case 'bool':
      return typeof value === 'boolean' ? null : 'On or off.'
    case 'string': {
      if (typeof value !== 'string') return 'Text.'
      const text = value.trim()
      if (field.maxLength !== undefined && text.length > field.maxLength) return `At most ${field.maxLength} characters.`
      if (field.required && text.length === 0) return 'Required.'
      if (text.length > 0 && field.pattern && !new RegExp(field.pattern).test(text)) return 'Not in the expected format.'
      return null
    }
    case 'stringList': {
      if (!Array.isArray(value)) return 'A list.'
      if (value.some((v) => typeof v !== 'string' || v.trim() === '' || (field.maxLength !== undefined && v.trim().length > field.maxLength)))
        return 'Each item is non-empty text.'
      if (field.maxItems !== undefined && value.length > field.maxItems) return `At most ${field.maxItems} items.`
      return null
    }
    case 'date':
      return isIsoDate(value) ? null : 'A real date, yyyy-mm-dd.'
    case 'range':
      return typeof value === 'string' && isValidRange(value) ? null : 'A date range.'
    case 'explorerQuery':
      return explorerQueryError(value)
  }
  return `Unknown kind for ${name}.`
}

/**
 * A structural check only; the server runs the Explorer's own validator. What matters here is the
 * rule that keeps identifiers out of a shared definition: none in where, groupBy, columns or sort.
 */
function explorerQueryError(value: unknown): string | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return 'Build a query.'
  const q = value as Record<string, unknown>
  if (q.dataset !== 'Bindings' && q.dataset !== 'Events') return 'Choose a dataset.'
  if (!Array.isArray(q.measures) || q.measures.length === 0) return 'Add at least one measure: a dashboard query counts or summarises.'

  const fields: string[] = []
  const walk = (node: unknown) => {
    if (!node || typeof node !== 'object') return
    const n = node as Record<string, unknown>
    if (typeof n.field === 'string') fields.push(n.field)
    if (Array.isArray(n.children)) n.children.forEach(walk)
  }
  walk(q.where)
  for (const list of [q.groupBy, q.columns]) if (Array.isArray(list)) fields.push(...list.filter((f): f is string => typeof f === 'string'))
  if (Array.isArray(q.sort)) for (const s of q.sort) if (s && typeof s === 'object' && typeof (s as { field?: unknown }).field === 'string') fields.push((s as { field: string }).field)

  return fields.some((f) => IDENTIFIER_FIELDS.has(f))
    ? 'A dashboard query may not filter, group or show numbers, SIMs or handsets; count them instead.'
    : null
}

/** What is wrong with a config, field by field. */
export function validateConfig(type: string, config: WidgetConfig): FieldError[] {
  const spec = typeSpec(type)
  if (!spec) return [{ field: 'type', message: 'This widget type is not available in this version.' }]

  const errors: FieldError[] = []
  for (const name of Object.keys(config)) {
    if (!Object.hasOwn(spec.config, name)) errors.push({ field: name, message: 'Not a setting of this widget.' })
  }
  for (const [name, field] of Object.entries(spec.config)) {
    const message = fieldError(name, config[name], field)
    if (message) errors.push({ field: name, message })
  }
  return errors
}

function lensErrors(lens: DashboardLens, path: string, type?: string): FieldError[] {
  const spec = type ? typeSpec(type) : undefined
  const errors: FieldError[] = []
  for (const [key, value] of Object.entries(lens)) {
    if (value === undefined || value === null) continue
    const field = `${path}.${key}`
    if (!(LENS_KEYS as readonly string[]).includes(key)) {
      errors.push({ field, message: 'Not a view setting.' })
      continue
    }
    if (spec) {
      const supported =
        key === 'countBy'
          ? supportsCountBy(spec) && spec.measures.includes(value as string)
          : key === 'range'
            ? supportsRange(spec)
            : (spec.filters as string[]).includes(key)
      if (!supported) {
        errors.push({ field, message: 'This widget cannot take this setting.' })
        continue
      }
    }
    const ok =
      key === 'countBy'
        ? CONTRACT.lens.countBy.includes(value as string)
        : key === 'includeUnknownDevice'
          ? typeof value === 'boolean'
          : key === 'range'
            ? isValidRange(value as string)
            : key === 'tac'
              ? typeof value === 'string' && new RegExp(CONTRACT.lens.tacPattern).test(value)
              : typeof value === 'string' && value.trim() !== '' && value.trim().length <= CONTRACT.lens.textFilterMaxLength
    if (!ok) errors.push({ field, message: 'Not a value this setting takes.' })
  }
  return errors
}

/** What is wrong with one widget instance. */
export function validateInstance(instance: WidgetInstance): FieldError[] {
  const errors: FieldError[] = []
  const spec = typeSpec(instance.type)
  if (!INSTANCE_ID.test(instance.id)) errors.push({ field: 'id', message: 'Not a widget id.' })
  if (!spec) return [...errors, { field: 'type', message: 'This widget type is not available in this version.' }]
  if (instance.configVersion !== spec.configVersion)
    errors.push({ field: 'configVersion', message: 'Saved by a different version; upgrade it before saving.' })
  if ((instance.title?.trim().length ?? 0) > CONTRACT.limits.titleMaxLength)
    errors.push({ field: 'title', message: `At most ${CONTRACT.limits.titleMaxLength} characters.` })
  if ((instance.subtitle?.trim().length ?? 0) > CONTRACT.limits.subtitleMaxLength)
    errors.push({ field: 'subtitle', message: `At most ${CONTRACT.limits.subtitleMaxLength} characters.` })

  errors.push(...validateConfig(instance.type, instance.config).map((e) => ({ ...e, field: `config.${e.field}` })))
  errors.push(...lensErrors(instance.overrides ?? {}, 'overrides', instance.type))

  const { x, y, w, h } = instance.layout
  const columns = CONTRACT.limits.gridColumns
  if (![x, y, w, h].every(Number.isInteger)) errors.push({ field: 'layout', message: 'Positions are whole numbers.' })
  else {
    if (x < 0 || x + w > columns) errors.push({ field: 'layout.x', message: `Inside the ${columns} columns.` })
    if (y < 0 || y > CONTRACT.limits.maxRows) errors.push({ field: 'layout.y', message: 'Inside the grid.' })
    if (w < spec.size.minW || w > columns) errors.push({ field: 'layout.w', message: `Between ${spec.size.minW} and ${columns} columns wide.` })
    if (h < spec.size.minH || h > CONTRACT.limits.maxWidgetHeight)
      errors.push({ field: 'layout.h', message: `Between ${spec.size.minH} and ${CONTRACT.limits.maxWidgetHeight} rows tall.` })
  }
  return errors
}

export interface DefinitionLimits {
  maxWidgets: number
}

/** What is wrong with a whole definition, prefixed with each widget's position. */
export function validateDefinition(definition: DashboardDefinition, limits: DefinitionLimits = { maxWidgets: 24 }): FieldError[] {
  const errors: FieldError[] = []
  if (definition.schemaVersion !== 1) errors.push({ field: 'schemaVersion', message: 'Saved by a newer version.' })
  if (!CONTRACT.lens.refreshMinutes.includes(definition.refreshMinutes))
    errors.push({ field: 'refreshMinutes', message: 'Not an allowed interval.' })
  errors.push(...lensErrors(definition.defaults ?? {}, 'defaults'))
  if (definition.widgets.length > limits.maxWidgets)
    errors.push({ field: 'widgets', message: `A dashboard holds at most ${limits.maxWidgets} widgets.` })

  const seen = new Set<string>()
  definition.widgets.forEach((w, i) => {
    if (seen.has(w.id)) errors.push({ field: `widgets[${i}].id`, message: 'Two widgets share an id.' })
    seen.add(w.id)
    errors.push(...validateInstance(w).map((e) => ({ ...e, field: `widgets[${i}].${e.field}` })))
  })
  return errors
}
