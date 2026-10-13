/**
 * How many columns a row of KPI cards takes at a width.
 *
 * All in one row when each card gets at least 10rem; otherwise rows of three, then two, then one -
 * the steps the dashboard page's KPI row took (two, three, six), chosen by the row's own width rather
 * than the window's, because a widget can be narrower than the page. Never a count that leaves one
 * card alone under a full row, as an auto-fit grid does at 5 + 1.
 */
export const KPI_MIN_WIDTH = 160
export const KPI_GAP = 12

function fits(columns: number, width: number): boolean {
  return width >= columns * KPI_MIN_WIDTH + (columns - 1) * KPI_GAP
}

export function kpiRowColumns(cards: number, width: number): number {
  if (cards <= 1) return 1
  // Not measured yet (first render, or a hidden tab): one row, as on a wide screen.
  if (width <= 0) return cards
  if (fits(cards, width)) return cards
  for (const columns of [3, 2]) if (columns < cards && fits(columns, width)) return columns
  return 1
}
