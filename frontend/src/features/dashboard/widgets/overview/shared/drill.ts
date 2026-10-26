/**
 * Drill-down from a chart or table: sets a lens filter on the dashboard, which the URL records, so
 * Back undoes it.
 *
 * Only while viewing - a click in edit mode, a preview or an export does nothing - and only where
 * the widget's config allows it. An "(unknown …)" bucket is the absence of a value, not one to filter
 * by, so it is never drilled into.
 */
import { inject } from 'vue'
import { DASHBOARD_CONTEXT, type WidgetMode } from '@/features/dashboard/types/widget'

export type DrillKey = 'vendor' | 'deviceType' | 'operatingSystem' | 'tac'

export function isDrillableValue(value: string): boolean {
  return value.trim() !== '' && !value.startsWith('(unknown')
}

export function useDrill(mode: () => WidgetMode) {
  const context = inject(DASHBOARD_CONTEXT, null)

  /** Whether clicks drill in at all, for the cursor and the caption's "Click to filter". */
  const canDrill = (allowed: boolean): boolean => allowed && mode() === 'view' && context !== null

  function drill(key: DrillKey | null, value: string, allowed: boolean): void {
    if (key === null || !canDrill(allowed) || !isDrillableValue(value)) return
    context!.setLensFilter(key, value)
  }

  return { canDrill, drill }
}
