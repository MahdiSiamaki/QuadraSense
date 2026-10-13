/**
 * A bare widget's menu, handed to the widget to draw inside its own card.
 *
 * A bare widget is itself a card (a single KPI). Its menu belongs where every other card has one:
 * the card's top-right corner, inside its border. Drawn beside the card it made the card narrower
 * than the grid column, and its right edge stopped short of every other card's. So in view mode the
 * frame draws no menu of its own and provides it here; KpiWidget puts it in the KpiCard's header.
 */
import type { ComputedRef, InjectionKey } from 'vue'
import type { ActionMenuItem } from './ActionMenu.vue'

export interface FrameActions {
  label: string
  items: ActionMenuItem[]
  select: (key: string) => void
}

export const FRAME_ACTIONS: InjectionKey<ComputedRef<FrameActions | null>> = Symbol('dashboard-frame-actions')
