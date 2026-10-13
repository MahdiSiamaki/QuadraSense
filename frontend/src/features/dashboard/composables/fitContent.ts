/**
 * How a content-sized card (definition `height: 'content'`) tells the grid what it needs.
 *
 * The card's frame wraps the widget in an element that always has the content's own height, even
 * while the card is stretched to the rest of its row, and hands it to the grid that provides this
 * registry. The grid watches those elements and sizes each card to fit. Outside a grid - the Add
 * widget preview, an export - nothing is provided and nothing is watched.
 */
import type { InjectionKey } from 'vue'

export interface FitContentRegistry {
  observe(content: HTMLElement): void
  unobserve(content: HTMLElement): void
}

export const FIT_CONTENT: InjectionKey<FitContentRegistry> = Symbol('dashboard-fit-content')
