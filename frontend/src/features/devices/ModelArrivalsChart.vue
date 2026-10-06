<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { ModelArrivals } from '@/api/modelArrivals'
import { chartColor } from '@/lib/chart-colors'
import { formatCompact, formatDate, formatFull, formatMonth } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { tooltipBounds } from '@/lib/chart-tooltip'

echarts.use([BarChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

/**
 * Models first seen per month (or week): the ones GSMA knows, and the TACs it does not, stacked as
 * two parts of one count. The first weeks after the data starts are mostly models the initial
 * dump's month happened to miss, and the chart's caption says so where it is read.
 */
const props = withDefaults(defineProps<{ data: ModelArrivals; height?: string }>(), { height: '15rem' })

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

function label(iso: string): string {
  return props.data.grain === 'month' ? formatMonth(iso) : formatDate(iso)
}

function render() {
  if (!chart.value) return
  const muted = chartColor('--c-text-muted')
  const border = chartColor('--c-border')
  const periods = props.data.periods

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: chartColor('--c-text-secondary'), fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 28, bottom: 8, containLabel: true },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        backgroundColor: chartColor('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: chartColor('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const p = periods[params[0]?.dataIndex ?? 0]
          if (!p) return ''
          return [
            `<strong>${label(p.periodStart)}</strong>`,
            `known to GSMA &nbsp; ${formatFull(p.knownModels)}`,
            `TAC not in GSMA &nbsp; ${formatFull(p.models - p.knownModels)}`,
          ].join('<br/>')
        },
      },
      xAxis: {
        type: 'category',
        data: periods.map((p) => label(p.periodStart)),
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        name: 'models first seen',
        nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      series: [
        {
          name: 'Known to GSMA',
          type: 'bar',
          stack: 'models',
          data: periods.map((p) => p.knownModels),
          itemStyle: { color: chartColor('--viz-1') },
          barMaxWidth: 28,
        },
        {
          name: 'TAC not in GSMA',
          type: 'bar',
          stack: 'models',
          data: periods.map((p) => p.models - p.knownModels),
          itemStyle: { color: chartColor('--viz-null'), borderRadius: [4, 4, 0, 0] },
          barMaxWidth: 28,
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
watch(() => props.data, render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" role="img" aria-label="Models first seen per period" />
</template>
