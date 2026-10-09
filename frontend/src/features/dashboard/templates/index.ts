/**
 * The read-only templates (ADR-016 §7): code, versioned, never seeded per person.
 */
import { newWidgetId, type DashboardDefinition } from '../types/definition'
import { requiredPermissions } from '../registry/contract'
import { compact } from '../composables/layout'
import { dataOperations } from './dataOperations'
import { devicePopulation } from './devicePopulation'
import { securityRisk } from './securityRisk'
import { support } from './support'
import type { DashboardTemplate } from './types'

export type { DashboardTemplate } from './types'

export const TEMPLATES: readonly DashboardTemplate[] = [devicePopulation, securityRisk, support, dataOperations]

/** What `/` opens when a person has chosen nothing. */
export const DEFAULT_TEMPLATE_KEY = devicePopulation.key

export function getTemplate(key: string): DashboardTemplate | undefined {
  return TEMPLATES.find((t) => t.key === key)
}

export interface OmittedWidget {
  title: string
  missing: string[]
}

/**
 * The widgets of a template this person may use. Those they may not are left out and named - never
 * drawn as cards that would query and be refused: a template grants nothing.
 */
export function visibleTemplateDefinition(
  template: DashboardTemplate,
  can: (permission: string) => boolean,
): { definition: DashboardDefinition; omitted: OmittedWidget[] } {
  const omitted: OmittedWidget[] = []
  const widgets = template.definition.widgets.filter((w) => {
    const missing = requiredPermissions(w.type, w.config).filter((p) => !can(p))
    if (missing.length) omitted.push({ title: w.title ?? w.type, missing })
    return missing.length === 0
  })
  return { definition: { ...template.definition, widgets: compact(widgets) }, omitted }
}

/**
 * A private copy of a template: the widgets the person may use, packed, each with a new id, so a copy
 * never shares ids with the template or with another copy.
 */
export function instantiateTemplate(
  template: DashboardTemplate,
  can: (permission: string) => boolean,
): { definition: DashboardDefinition; omitted: OmittedWidget[] } {
  const { definition, omitted } = visibleTemplateDefinition(template, can)
  const copy = JSON.parse(JSON.stringify(definition)) as DashboardDefinition
  const taken = new Set<string>()
  for (const w of copy.widgets) {
    let id = newWidgetId()
    while (taken.has(id)) id = newWidgetId()
    taken.add(id)
    w.id = id
  }
  return { definition: copy, omitted }
}

/** How a copy records where it came from: key@version. */
export function templateReference(template: DashboardTemplate): string {
  return `${template.key}@${template.version}`
}
