/**
 * The widget registry (ADR-016 §3): every widget definition the build knows, with its contract
 * attached.
 *
 * Definitions are code; each group exports them from `widgets/<group>/index.ts` and this file
 * collects every group at build time. What a type may be configured with comes from the contract,
 * which the server enforces too - a definition whose type the contract does not name is left out,
 * loudly, rather than offered and then refused on save.
 *
 * The registry runs no plugin, no user JavaScript and no SQL: it is a typed table in the bundle.
 */
import type { WidgetConfig } from '../types/definition'
import type { ResolvedWidgetDefinition, WidgetDefinition, WidgetIconName } from '../types/widget'
import { CONTRACT, defaultConfig, typeSpec, type WidgetGroupId } from './contract'

interface WidgetModule {
  definitions?: WidgetDefinition<WidgetConfig>[]
}

const modules = import.meta.glob<WidgetModule>('../widgets/*/index.ts', { eager: true })

function resolve(): Map<string, ResolvedWidgetDefinition> {
  const registry = new Map<string, ResolvedWidgetDefinition>()
  for (const [path, module] of Object.entries(modules)) {
    for (const definition of module.definitions ?? []) {
      const spec = typeSpec(definition.type)
      if (!spec) {
        console.error(`Dashboard widget ${definition.type} (${path}) is not in widget-contract.json and is not offered.`)
        continue
      }
      if (registry.has(definition.type)) {
        console.error(`Dashboard widget ${definition.type} is defined twice; ${path} is ignored.`)
        continue
      }
      registry.set(definition.type, { ...definition, spec, group: spec.group })
    }
  }
  return registry
}

const REGISTRY = resolve()

export function getWidget(type: string): ResolvedWidgetDefinition | undefined {
  return REGISTRY.get(type)
}

export function isRegistered(type: string): boolean {
  return REGISTRY.has(type)
}

export function allWidgets(): ResolvedWidgetDefinition[] {
  return [...REGISTRY.values()]
}

/** One card in the Add widget catalogue: a type, or one of its presets. */
export interface CatalogEntry {
  /** `type` or `type:preset`. Unique. */
  key: string
  type: string
  presetKey: string | null
  name: string
  description: string
  group: WidgetGroupId
  tags: string[]
  aliases: string[]
  icon: WidgetIconName
  /** The type's default config with the preset applied. */
  config: WidgetConfig
  /** The title a new instance gets, or null for the type's name. */
  title: string | null
}

/**
 * The catalogue: each type once, then each of its presets. A preset is a starting config, never a
 * type of its own - "Top models" is `device.top` with a dimension chosen.
 */
export function catalogEntries(): CatalogEntry[] {
  const entries: CatalogEntry[] = []
  for (const definition of REGISTRY.values()) {
    const base = defaultConfig(definition.type)
    entries.push({
      key: definition.type,
      type: definition.type,
      presetKey: null,
      name: definition.name,
      description: definition.description,
      group: definition.group,
      tags: definition.tags,
      aliases: definition.aliases,
      icon: definition.icon,
      config: base,
      title: null,
    })
    for (const preset of definition.presets ?? []) {
      entries.push({
        key: `${definition.type}:${preset.key}`,
        type: definition.type,
        presetKey: preset.key,
        name: preset.name,
        description: preset.description,
        group: preset.group ?? definition.group,
        tags: [...definition.tags, ...(preset.tags ?? [])],
        aliases: [...definition.aliases, ...(preset.aliases ?? [])],
        icon: definition.icon,
        config: { ...base, ...structuredClone(preset.config) },
        title: preset.title ?? preset.name,
      })
    }
  }
  return entries
}

/** The groups in contract order, each with how many catalogue entries it holds. */
export function catalogGroups(entries: CatalogEntry[] = catalogEntries()) {
  return CONTRACT.groups.map((group) => ({
    ...group,
    count: entries.filter((e) => e.group === group.id).length,
  }))
}
