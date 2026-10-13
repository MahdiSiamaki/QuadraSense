/**
 * The API filters and count unit a widget's lens asks for.
 *
 * One builder for every widget that reads /kpi, /top or /distribution, so instances with equal lenses
 * build equal query keys and share one request (ADR-016 §12: six KPI cards are one /kpi request).
 * The lens carries only the fields the widget's type supports (`composables/lens.ts`), so a field
 * left out there is left out here.
 *
 * Never an MSISDN prefix: `msisdnPrefix` is not a lens field and no widget sends one.
 */
import type { CountBy, DashboardFilters } from '@/api/dashboard'
import type { EffectiveLens } from '@/features/dashboard/types/widget'

export function lensFilters(lens: EffectiveLens): DashboardFilters {
  const filters: DashboardFilters = {}
  if (lens.vendor) filters.vendor = lens.vendor
  if (lens.deviceType) filters.deviceType = lens.deviceType
  if (lens.operatingSystem) filters.operatingSystem = lens.operatingSystem
  if (lens.tac) filters.tac = lens.tac
  filters.includeUnknownDevice = lens.includeUnknownDevice ?? true
  return filters
}

export function lensCountBy(lens: EffectiveLens): CountBy {
  return lens.countBy ?? 'bindings'
}
