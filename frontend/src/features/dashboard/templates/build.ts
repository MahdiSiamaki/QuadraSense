import { typeSpec } from '../registry/contract'
import { normalizeConfig } from '../registry/validate'
import type { WidgetConfig, WidgetInstance } from '../types/definition'

/** One template widget, the type's defaults filled in, so a template states only what it chooses. */
export function widget(
  id: string,
  type: string,
  title: string | null,
  config: WidgetConfig,
  x: number,
  y: number,
  w?: number,
  h?: number,
): WidgetInstance {
  const spec = typeSpec(type)
  if (!spec) throw new Error(`Template widget ${id}: unknown type ${type}`)
  return {
    id,
    type,
    configVersion: spec.configVersion,
    title,
    subtitle: null,
    config: normalizeConfig(type, config),
    overrides: {},
    layout: { x, y, w: w ?? spec.size.w, h: h ?? spec.size.h },
  }
}
