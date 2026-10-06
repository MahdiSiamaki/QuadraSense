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
