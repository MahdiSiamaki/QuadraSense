/**
 * What every risk widget reads first: the risk status - whether the measures are built, which
 * rules exist and how they are configured, and whether the published run is stale.
 *
 * One query (`['risk', 'status']`) shared by every risk widget on the dashboard, and in an export
 * the `status` source every risk snapshot carries.
 */
import { computed } from 'vue'
import { useRiskStatus, type RiskStatus } from '@/api/risk'
import { useQueryActive, useWidgetData } from '@/features/dashboard/composables/useWidgetData'
import type { WidgetViewProps } from '@/features/dashboard/types/widget'

export const RULES = [
  'SharedImeiSims30',
  'SharedImeiNumbers30',
  'SharedImeiSimsEver',
  'SharedImeiSimsNotRemoved',
  'HighDeviceCount30',
  'RapidDeviceChange7',
  'Randomisation20',
  'RepeatedSimChange7',
] as const

export type RuleName = (typeof RULES)[number]

export function useRiskSource(props: WidgetViewProps<any>) {
  const active = useQueryActive(props)
  const query = useRiskStatus(active)
  const status = useWidgetData(props, query, (raw) => (raw as { status: RiskStatus }).status)
  const ready = computed(() => status.data.value?.ready === true)
  return { active, status, ready }
}
