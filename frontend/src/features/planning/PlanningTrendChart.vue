<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { formatCompact, formatFull } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { chartColor } from '@/lib/chart-colors'
import { tooltipBounds } from '@/lib/chart-tooltip'

echarts.use([LineChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

/**
 * A planning class over deliveries: one line per class, on one axis, because they are counts of
 * the same unit and how they move against each other is the point.
 *
 * Points are deliveries, not calendar days: a per-number figure exists only for a delivery a
 * planning run was built for, so the gaps between them are real and drawn as such.
 */
const props = withDefaults(
  defineProps<{
    /** One per delivery: its label and each class's value. */
    points: Array<{ label: string; values: Record<string, number> }>
    /** The classes, in drawing order, with the token each is drawn in. */
    series: Array<{ key: string; label: string; color: string }>
    /** What the values count, said in the tooltip. */
    unit: string
    height?: string
  }>(),
  { height: '16rem' },
)

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

function render() {
  if (!chart.value) return

  const text = chartColor('--c-text-secondary')
  const muted = chartColor('--c-text-muted')
  const border = chartColor('--c-border')

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: text, fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 30, bottom: 24, containLabel: true },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        backgroundColor: chartColor('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: chartColor('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const point = props.points[params[0]?.dataIndex ?? 0]
          if (!point) return ''
          return [
            `<strong>${point.label}</strong>`,
            ...props.series.map((s) => `${s.label} &nbsp; ${formatFull(point.values[s.key] ?? 0)} ${props.unit}`),
          ].join('<br/>')
        },
      },
      xAxis: {
        type: 'category',
        data: props.points.map((p) => p.label),
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true, alignMinLabel: 'left', alignMaxLabel: 'right' },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      series: props.series.map((s) => ({
        name: s.label,
        type: 'line',
        data: props.points.map((p) => p.values[s.key] ?? 0),
        itemStyle: { color: chartColor(s.color) },
        lineStyle: { width: 2 },
        // A point a run exists for is marked, so a single run reads as one figure, not as a line.
        symbol: 'circle',
        symbolSize: props.points.length > 40 ? 0 : 5,
      })),
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

watch(() => [props.points, props.series, props.unit], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" />
</template>
