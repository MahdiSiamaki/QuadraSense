<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
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

echarts.use([
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
  }>(),
  { height: '18rem', flagged: () => new Map<string, string>() },
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

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: text, fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 30, bottom: 40, containLabel: true },
      tooltip: {
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
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      dataZoom: [{ type: 'inside', start: 66, end: 100 }],
      series: [
        {
          name: 'Handset changes',
          type: 'line',
          data: value((d) => d.deviceChanges),
          itemStyle: { color: cssVar('--viz-1') },
          areaStyle: { opacity: 0.12 },
          lineStyle: { width: 2 },
          symbol: 'none',
          markArea: feedQualityMarkArea(
            days.map((d) => d.date),
            props.flagged,
          ),
        },
        {
          name: 'SIM changes',
          type: 'line',
          data: value((d) => d.simChanges),
          itemStyle: { color: cssVar('--viz-5') },
          areaStyle: { opacity: 0.12 },
          lineStyle: { width: 2 },
          symbol: 'none',
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
