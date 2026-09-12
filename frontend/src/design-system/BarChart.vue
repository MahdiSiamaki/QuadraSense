<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { formatCompact, formatFull } from '@/lib/format'
import { useTheme } from '@/lib/theme'

/*
  Only the pieces this chart needs are registered. Importing all of ECharts would
  pull ~1 MB into the bundle for a bar chart; tree-shaken registration keeps it to
  what is actually drawn.

  Canvas rather than SVG: these charts routinely plot long-tailed categorical data
  and canvas stays smooth where SVG starts creating thousands of DOM nodes.
*/
echarts.use([BarChart, GridComponent, TooltipComponent, CanvasRenderer])

const props = withDefaults(
  defineProps<{
    data: Array<{ key: string; count: number; percent: number }>
    /** Horizontal bars are correct for ranked categories: labels stay readable. */
    horizontal?: boolean
    height?: string
  }>(),
  { horizontal: true, height: '18rem' },
)

const emit = defineEmits<{ select: [key: string] }>()

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function render() {
  if (!chart.value) return

  // Read colours from the design tokens rather than hardcoding, so the chart
  // follows the theme automatically instead of needing its own palette.
  const text = cssVar('--c-text-secondary')
  const muted = cssVar('--c-text-muted')
  const border = cssVar('--c-border')
  const accent = cssVar('--viz-1')
  const nullColour = cssVar('--viz-null')

  const categories = props.data.map((d) => d.key)
  const values = props.data.map((d) => ({
    value: d.count,
    // The "unknown device" bucket is ~7% of rows. It is shown, because hiding it
    // would understate every total, but drawn in grey so it reads as absence of
    // information rather than as another vendor.
    itemStyle: { color: d.key.startsWith('(unknown') ? nullColour : accent },
  }))

  chart.value.setOption(
    {
      animation: false,
      grid: { left: 8, right: 56, top: 8, bottom: 8, containLabel: true },
      tooltip: {
        trigger: 'item',
        backgroundColor: cssVar('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: cssVar('--c-text'), fontSize: 12 },
        // The unit is named explicitly. A bare number here reads as "handsets" to most
        // people, and bindings run ~34% higher than handsets for a big vendor.
        formatter: (p: { name: string; value: number; dataIndex: number }) =>
          `${p.name}<br/><strong>${formatFull(p.value)}</strong> bindings`
          + ` (${props.data[p.dataIndex]?.percent.toFixed(2)}%)`,
      },
      xAxis: props.horizontal
        ? {
            type: 'value',
            axisLabel: { color: muted, fontSize: 11, formatter: (v: number) => formatCompact(v) },
            splitLine: { lineStyle: { color: border, type: 'dashed' } },
          }
        : { type: 'category', data: categories, axisLabel: { color: text, fontSize: 11 } },
      yAxis: props.horizontal
        ? {
            type: 'category',
            data: categories,
            inverse: true,
            axisLabel: { color: text, fontSize: 12 },
            axisLine: { show: false },
            axisTick: { show: false },
          }
        : {
            type: 'value',
            axisLabel: { color: muted, fontSize: 11, formatter: (v: number) => formatCompact(v) },
            splitLine: { lineStyle: { color: border, type: 'dashed' } },
          },
      series: [
        {
          type: 'bar',
          data: values,
          barMaxWidth: 18,
          itemStyle: { borderRadius: props.horizontal ? [0, 3, 3, 0] : [3, 3, 0, 0] },
          label: {
            show: true,
            position: props.horizontal ? 'right' : 'top',
            color: muted,
            fontSize: 11,
            formatter: (p: { value: number }) => formatCompact(p.value),
          },
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
  chart.value.on('click', (p: { name: string }) => emit('select', p.name))
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
  <div ref="container" :style="{ height }" class="w-full" />
</template>
