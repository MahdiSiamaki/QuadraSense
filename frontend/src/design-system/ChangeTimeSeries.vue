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
  }>(),
  { height: '20rem', missingDates: () => [], flagged: () => new Map<string, string>() },
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
      grid: { left: 8, right: 8, top: 52, bottom: 46, containLabel: true },
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
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: [
        {
          type: 'value',
          name: 'daily change',
          nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
          axisLabel: {
            color: muted,
            fontSize: 10,
            formatter: (v: number) => formatCompact(Math.abs(v)),
          },
          splitLine: { lineStyle: { color: border, type: 'dashed' } },
        },
        {
          type: 'value',
          name: 'active bindings',
          nameTextStyle: { color: muted, fontSize: 10, align: 'right' },
          axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
          splitLine: { show: false },
          // Not starting at zero, on purpose: the population moves by a fraction of a percent
          // a day, and a zero-based axis would render that as a flat line.
          scale: true,
        },
      ],
      // 133 days is more than fits legibly; the zoom starts on the most recent third and
      // the whole range stays reachable by dragging.
      dataZoom: [
        { type: 'inside', start: 66, end: 100 },
        {
          type: 'slider',
          start: 66,
          end: 100,
          height: 18,
          bottom: 4,
          borderColor: border,
          fillerColor: withAlpha(cssVar('--c-accent'), 0.13),
          handleStyle: { color: cssVar('--c-accent') },
          textStyle: { color: muted, fontSize: 9 },
        },
      ],
      series: [
        {
          name: 'Added',
          type: 'bar',
          stack: 'flow',
          data: value((d) => d.added),
          itemStyle: { color: added },
          barMaxWidth: 14,
          markArea: feedQualityMarkArea(dates, props.flagged),
        },
        {
          name: 'Removed',
          type: 'bar',
          stack: 'flow',
          // Negative so the two directions separate around zero and the net reads as the
          // visible imbalance.
          data: value((d) => -d.removed),
          itemStyle: { color: removed },
          barMaxWidth: 14,
        },
        {
          name: 'Active bindings',
          type: 'line',
          yAxisIndex: 1,
          data: value((d) => d.cumulative),
          itemStyle: { color: total },
          lineStyle: { width: 2 },
          symbol: 'none',
          z: 5,
        },
      ],
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

watch(() => [props.data, props.flagged], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" />
</template>
