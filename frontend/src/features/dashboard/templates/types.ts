import type { DashboardDefinition } from '../types/definition'

/**
 * A read-only dashboard shipped with the code (ADR-016 §7). Using one makes an independent private
 * copy with new widget ids; a template is never stored per person and never changes anybody's
 * dashboard when it changes.
 */
export interface DashboardTemplate {
  /** Stable, also the value of a person's default when they choose it. */
  key: string
  /** Bumped when the template's content changes; recorded on copies as key@version. */
  version: number
  name: string
  description: string
  definition: DashboardDefinition
}
