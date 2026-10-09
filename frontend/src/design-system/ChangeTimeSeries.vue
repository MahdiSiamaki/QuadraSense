<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart } from 'echarts/charts'
import {
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DataZoomComponent,
  MarkLineComponent,
  MarkAreaComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { formatCompact, formatFull, formatSigned } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { chartColor, withAlpha } from '@/lib/chart-colors'
import { fillCalendar, isMissing } from '@/lib/calendar'
import { feedQualityMarkArea, feedQualityTooltip } from '@/lib/feed-quality-bands'
import type { DailyChange } from '@/api/dashboard'
import { tooltipBounds } from '@/lib/chart-tooltip'

echarts.use([
  BarChart, LineChart, GridComponent, TooltipComponent, LegendComponent,
  DataZoomComponent, MarkLineComponent, MarkAreaComponent, CanvasRenderer,
])

/**
 * Daily flow and running stock on one chart.
 *
 * Adds and removes are drawn as opposing bars around zero, and the running active-binding
 * total as a line on a second axis. That pairing is the point: the bars show how much moved
 * on a day, the line shows whether the population actually grew. A day with 4M adds and 4M
 * removes is enormous churn and zero growth, and either series alone would hide half of that.
 *
 * Removes are plotted negative so the two directions separate visually instead of competing
 * for the same space, and the net is the visible imbalance around the zero line.
 *
 * The second axis is a deliberate exception to the usual advice against them. Here it is
 * defensible because the two series are different quantities in the same unit (bindings
 * moved per day versus bindings held in total) and differ by roughly two orders of
 * magnitude, so a shared axis would flatten the bars to nothing.
 */
const props = withDefaults(
  defineProps<{
    data: DailyChange[]
    height?: string
    /** Missing days are drawn as gaps, not interpolated. */
    missingDates?: string[]
    /** Days the feed itself looked wrong, date to reason: shaded, and said in the tooltip. */
    flagged?: ReadonlyMap<string, string>
    /**
     * Which series to draw. The default is the dashboard's original three: adds and removes around
     * zero, and the running total on its own axis. `net` is the day's added minus removed.
     */
    series?: Array<'added' | 'removed' | 'net' | 'cumulative'>
    /** The day's flow as bars (the default) or as lines. The running total is always a line. */
    chart?: 'bar' | 'line'
    /** Where the zoom window opens, in percent of the days drawn; 66 opens on the most recent third. */
    zoomStart?: number
  }>(),
  {
    height: '20rem',
    missingDates: () => [],
    flagged: () => new Map<string, string>(),
    series: () => ['added', 'removed', 'cumulative'],
    chart: 'bar',
    zoomStart: 66,
  },
)

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

/*
  Tokens are oklch, and ECharts needs a colour it can compute with, not only paint. Reading the
  custom property directly returned oklch, which drew correctly and then made the hovered bar
  disappear - zrender cannot parse it, so the hover fill resolved to undefined. See chart-colors.
*/
const cssVar = chartColor

function render() {
  if (!chart.value) return

  const text = cssVar('--c-text-secondary')
  const muted = cssVar('--c-text-muted')
  const border = cssVar('--c-border')
  const added = cssVar('--viz-3')
  const removed = cssVar('--viz-6')
  const total = cssVar('--viz-1')

  // Every calendar day, not only the delivered ones: a day with no delivery gets a slot and no
  // value, so the bars leave a hole and the line breaks instead of joining across it.
  const days = fillCalendar(props.data)
  const dates = days.map((d) => d.date)
  const value = (pick: (row: DailyChange) => number) =>
    days.map((d) => (isMissing(d) ? null : pick(d)))

  const chosen = new Set(props.series)
  const flow = (['added', 'removed', 'net'] as const).filter((s) => chosen.has(s))
  const withTotal = chosen.has('cumulative') || flow.length === 0
  const asBars = props.chart === 'bar'

  // The day's flow. Bars stack around zero (adds up, removes down); net, when asked for, stands
  // beside that stack rather than in it, because it is the stack's imbalance, not a third part.
  function flowSeries(name: string, data: Array<number | null>, color: string, stacked: boolean) {
    return asBars
      ? { name, type: 'bar', ...(stacked ? { stack: 'flow' } : {}), data, itemStyle: { color }, barMaxWidth: 14 }
      : { name, type: 'line', data, itemStyle: { color }, lineStyle: { width: 2 }, symbol: 'none' }
  }

  const series: Array<Record<string, unknown>> = []
  if (chosen.has('added')) series.push(flowSeries('Added', value((d) => d.added), added, true))
  // Negative so the two directions separate around zero and the net reads as the
  // visible imbalance.
  if (chosen.has('removed')) series.push(flowSeries('Removed', value((d) => -d.removed), removed, true))
  if (chosen.has('net')) series.push(flowSeries('Net', value((d) => d.net), cssVar('--viz-2'), false))
  if (withTotal) {
    series.push({
      name: 'Active bindings',
      type: 'line',
      yAxisIndex: flow.length > 0 ? 1 : 0,
      data: value((d) => d.cumulative),
      itemStyle: { color: total },
      lineStyle: { width: 2 },
      symbol: 'none',
      z: 5,
    })
  }
  // The flagged-day bands ride on whichever series is drawn first.
  if (series[0]) series[0]['markArea'] = feedQualityMarkArea(dates, props.flagged)

  const flowAxis = {
    type: 'value',
    name: 'daily change',
    nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
    axisLabel: {
      color: muted,
      fontSize: 10,
      // Removes are drawn below zero but are not negative quantities, so the axis reads them as
      // magnitudes - unless net is drawn, which really can be negative.
      formatter: chosen.has('net') ? (v: number) => formatCompact(v) : (v: number) => formatCompact(Math.abs(v)),
    },
    splitLine: { lineStyle: { color: border, type: 'dashed' } },
  }
  const totalAxis = {
    type: 'value',
    name: 'active bindings',
    nameTextStyle: { color: muted, fontSize: 10, align: flow.length > 0 ? 'right' : 'left' },
    axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
    splitLine: flow.length > 0 ? { show: false } : { lineStyle: { color: border, type: 'dashed' } },
    // Not starting at zero, on purpose: the population moves by a fraction of a percent
    // a day, and a zero-based axis would render that as a flat line.
    scale: true,
  }
  const yAxis = flow.length === 0 ? [totalAxis] : withTotal ? [flowAxis, totalAxis] : [flowAxis]

  chart.value.setOption(
    {
      animation: false,
      legend: {
        top: 0,
        right: 0,
        textStyle: { color: text, fontSize: 11 },
        itemWidth: 10,
        itemHeight: 10,
      },
      // The axis titles are drawn above the plot, in the same corners as the legend. At top 30 the
      // right-hand title sat on top of the legend's last entry; 52 gives each its own row.
      grid: { left: 8, right: 8, top: 52, bottom: 46 },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        backgroundColor: cssVar('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: cssVar('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const row = days[params[0]?.dataIndex ?? 0]
          if (!row) return ''
          if (isMissing(row)) return `<strong>${row.date}</strong><br/>not delivered - no data`
          return [
            `<strong>${row.date}</strong>`,
            `added &nbsp; ${formatFull(row.added)}`,
            `removed &nbsp; ${formatFull(row.removed)}`,
            `net &nbsp; <strong>${formatSigned(row.net)}</strong>`,
            `active &nbsp; ${formatFull(row.cumulative)}`,
          ].join('<br/>') + feedQualityTooltip(row.date, props.flagged)
        },
      },
      xAxis: {
        type: 'category',
        data: dates,
        // The first and last dates align to the plot's edges: centred on the end ticks they ran off the canvas.
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true, alignMinLabel: 'left', alignMaxLabel: 'right' },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis,
      // 133 days is more than fits legibly; the zoom starts on the most recent third and
      // the whole range stays reachable by dragging.
      dataZoom: [
        { type: 'inside', start: props.zoomStart, end: 100 },
        {
          type: 'slider',
          start: props.zoomStart,
          end: 100,
          height: 18,
          bottom: 4,
          borderColor: border,
          fillerColor: withAlpha(cssVar('--c-accent'), 0.13),
          handleStyle: { color: cssVar('--c-accent') },
          textStyle: { color: muted, fontSize: 9 },
        },
      ],
      series,
    },
    { notMerge: true },
  )
}

let observer: ResizeObserver | null = null

onMounted(() => {
  if (!container.value) return
  chart.value = echarts.init(container.value, undefined, { renderer: 'canvas' })
  render()
  observer = new ResizeObserver(() => chart.value?.resize())
  observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  chart.value?.dispose()
  chart.value = null
})

watch(() => [props.data, props.flagged, props.series, props.chart, props.zoomStart], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" />
</template>
