<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart } from 'echarts/charts'
import {
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DataZoomComponent,
  MarkAreaComponent,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { formatCompact, formatFull } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { chartColor } from '@/lib/chart-colors'
import { fillCalendar, isMissing } from '@/lib/calendar'
import { feedQualityMarkArea, feedQualityTooltip } from '@/lib/feed-quality-bands'
import type { DailyChurn } from '@/api/dashboard'
import { tooltipBounds } from '@/lib/chart-tooltip'

echarts.use([
  BarChart,
  LineChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  DataZoomComponent,
  MarkAreaComponent,
  CanvasRenderer,
])

/**
 * SIM changes and handset changes per day.
 *
 * Lines rather than bars: both series are the same kind of quantity on the same scale, and
 * the question here is how the rates move and relate over time, which a line answers better
 * than a row of separate bars.
 *
 * Both share one axis, deliberately. They are directly comparable counts of subscribers, and
 * seeing that handset changes dwarf SIM changes is the main thing this chart has to say.
 */
const props = withDefaults(
  defineProps<{
    data: DailyChurn[]
    height?: string
    /** Days the feed itself looked wrong, date to reason: shaded, and said in the tooltip. */
    flagged?: ReadonlyMap<string, string>
    /** Which of the two to draw; both by default. */
    series?: Array<'simChanges' | 'deviceChanges'>
    /** Lines (the default) or bars. */
    chart?: 'line' | 'bar'
    /** Where the zoom window opens, in percent of the days drawn; 66 opens on the most recent third. */
    zoomStart?: number
  }>(),
  {
    height: '18rem',
    flagged: () => new Map<string, string>(),
    series: () => ['simChanges', 'deviceChanges'],
    chart: 'line',
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

  // Every calendar day, so a day not delivered breaks both lines rather than being bridged.
  const days = fillCalendar(props.data)
  const value = (pick: (row: DailyChurn) => number) =>
    days.map((d) => (isMissing(d) ? null : pick(d)))

  const chosen = new Set(props.series)
  const one = (name: string, data: Array<number | null>, color: string) =>
    props.chart === 'bar'
      ? { name, type: 'bar', data, itemStyle: { color }, barMaxWidth: 14 }
      : { name, type: 'line', data, itemStyle: { color }, areaStyle: { opacity: 0.12 }, lineStyle: { width: 2 }, symbol: 'none' }

  // Handset changes first, in the order the chart has always drawn them.
  const series: Array<Record<string, unknown>> = []
  if (chosen.has('deviceChanges') || chosen.size === 0) {
    series.push(one('Handset changes', value((d) => d.deviceChanges), cssVar('--viz-1')))
  }
  if (chosen.has('simChanges') || chosen.size === 0) {
    series.push(one('SIM changes', value((d) => d.simChanges), cssVar('--viz-5')))
  }
  // The flagged-day bands ride on whichever series is drawn first.
  if (series[0]) {
    series[0]['markArea'] = feedQualityMarkArea(
      days.map((d) => d.date),
      props.flagged,
    )
  }

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: text, fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 30, bottom: 40 },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        backgroundColor: cssVar('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: cssVar('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const row = days[params[0]?.dataIndex ?? 0]
          if (!row) return ''
          if (isMissing(row)) return `<strong>${row.date}</strong><br/>not delivered - no data`
          return [
            `<strong>${row.date}</strong>`,
            `SIM changes &nbsp; ${formatFull(row.simChanges)}`,
            `Handset changes &nbsp; ${formatFull(row.deviceChanges)}`,
          ].join('<br/>') + feedQualityTooltip(row.date, props.flagged)
        },
      },
      xAxis: {
        type: 'category',
        data: days.map((d) => d.date),
        // The first and last dates align to the plot's edges: centred on the end ticks they ran off the canvas.
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true, alignMinLabel: 'left', alignMaxLabel: 'right' },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      dataZoom: [{ type: 'inside', start: props.zoomStart, end: 100 }],
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
