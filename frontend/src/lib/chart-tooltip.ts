/**
 * What every chart's tooltip shares: it stays inside the chart, and long lines wrap.
 *
 * ECharts' default tooltip is `white-space: nowrap` and may leave its container. On a half-width
 * chart the flagged-day line ("Feed looked wrong this day: ...") made a tooltip 500-650px wide that
 * flipped left of the pointer and ran off the window's edge, cutting off the date it was about.
 */
export const tooltipBounds = {
  confine: true,
  extraCssText: 'white-space: normal; max-width: 20rem;',
} as const

/*
 * No chart sets grid.containLabel. Under ECharts 6, without `use(LegacyGridContainLabel)`, it keeps
 * only the axis LABELS inside the canvas: axis names were laid out at a fixed gap, clipped off the
 * canvas or drawn over rotated labels (the Risk distribution chart's unit name). The default
 * outerBounds layout contains labels and names both, and moves names clear of labels.
 */
