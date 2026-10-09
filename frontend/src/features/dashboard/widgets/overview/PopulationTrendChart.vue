<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, MarkAreaComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { formatCompact, formatDate, formatFull, formatMonth } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { chartColor } from '@/lib/chart-colors'
import { tooltipBounds } from '@/lib/chart-tooltip'
import { feedQualityMarkArea, feedQualityTooltip } from '@/lib/feed-quality-bands'
import type { TrendGrain } from './config'
import type { TrendBucket } from './populationTrend'

echarts.use([LineChart, GridComponent, TooltipComponent, MarkAreaComponent, CanvasRenderer])

/**
 * Active bindings over time: one line, by day, week or month.
 *
 * A bucket with no delivered day is a null, which ECharts draws as a break: the line never joins
 * across a day (or week) the source did not deliver. A delivered point with gaps on both sides would
 * have no segment to draw, so it alone carries a dot.
 *
 * The axis does not start at zero, on purpose: the population moves by a fraction of a percent a
 * day, and a zero-based axis would draw that as a flat line (the same reason as the running-total
 * axis on the daily-change chart).
 *
 * No dataZoom: the dashboard's date range chooses the window, and an inside zoom would take the
 * mouse wheel from a page that scrolls.
 *
 * The lifecycle is ChangeTimeSeries' with one difference: the chart is not initialised while its
 * container has no size. A card that mounts collapsed or off-layout would otherwise start a canvas at
 * 0x0; the ResizeObserver initialises it once there is room.
 */
const props = withDefaults(
  defineProps<{
    buckets: TrendBucket[]
    grain: TrendGrain
    chart: 'line' | 'area'
    /** Delivered days the feed itself looked wrong, date to reason. */
    flagged?: ReadonlyMap<string, string>
    height?: string
  }>(),
  { flagged: () => new Map<string, string>(), height: '100%' },
)

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

/*
  Tokens are oklch, and ECharts needs a colour it can compute with, not only paint. Reading the
  custom property directly returned oklch, which drew correctly and then made the hovered point
  disappear - zrender cannot parse it. See chart-colors.
*/
const cssVar = chartColor

function axisLabel(key: string): string {
  return props.grain === 'month' ? formatMonth(key) : key
}

function heading(bucket: TrendBucket): string {
  if (props.grain === 'month') return formatMonth(bucket.key)
  if (props.grain === 'week') return `Week of ${formatDate(bucket.key)}`
  return bucket.key
}

function flaggedLine(bucket: TrendBucket): string {
  if (bucket.flaggedDays.length === 0) return ''
  if (props.grain === 'day') return feedQualityTooltip(bucket.key, props.flagged)
  const reasons = [...new Set(bucket.flaggedDays.map((d) => props.flagged.get(d)).filter(Boolean))].join(', ')
  const days = bucket.flaggedDays.length
  return `<br/><em>Feed looked wrong on ${formatFull(days)} ${days === 1 ? 'day' : 'days'} of this ${props.grain}: ${reasons}. Figures may be distorted.</em>`
}

function render() {
  if (!chart.value) return

  const muted = cssVar('--c-text-muted')
  const border = cssVar('--c-border')
  const line = cssVar('--viz-1')

  const keys = props.buckets.map((b) => b.key)
  const flaggedKeys = new Map(props.buckets.filter((b) => b.flaggedDays.length > 0).map((b) => [b.key, 'flagged']))
  const data = props.buckets.map((b, i) => {
    if (b.value === null) return null
    const isolated = (props.buckets[i - 1]?.value ?? null) === null && (props.buckets[i + 1]?.value ?? null) === null
    return isolated ? { value: b.value, symbol: 'circle', symbolSize: 5 } : { value: b.value, symbol: 'none' }
  })

  chart.value.setOption(
    {
      animation: false,
      grid: { left: 8, right: 8, top: 28, bottom: 8 },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        backgroundColor: cssVar('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: cssVar('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const bucket = props.buckets[params[0]?.dataIndex ?? 0]
          if (!bucket) return ''
          if (bucket.value === null || bucket.lastDelivered === null) {
            return `<strong>${heading(bucket)}</strong><br/>${props.grain === 'day' ? 'not delivered' : 'no day delivered'} - no data`
          }
          const lines = [`<strong>${heading(bucket)}</strong>`, `active bindings &nbsp; ${formatFull(bucket.value)}`]
          if (props.grain !== 'day') {
            lines.push(
              `as of ${bucket.lastDelivered}, the last of ${formatFull(bucket.delivered)} delivered ${bucket.delivered === 1 ? 'day' : 'days'}`,
            )
          }
          return lines.join('<br/>') + flaggedLine(bucket)
        },
      },
      xAxis: {
        type: 'category',
        data: keys,
        // The first and last labels align to the plot's edges: centred on the end ticks they ran off the canvas.
        axisLabel: {
          color: muted,
          fontSize: 10,
          hideOverlap: true,
          alignMinLabel: 'left',
          alignMaxLabel: 'right',
          formatter: axisLabel,
        },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        name: 'active bindings',
        nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
        scale: true,
      },
      series: [
        {
          name: 'Active bindings',
          type: 'line',
          data,
          showSymbol: true,
          connectNulls: false,
          itemStyle: { color: line },
          lineStyle: { width: 2 },
          ...(props.chart === 'area' ? { areaStyle: { opacity: 0.15 } } : {}),
          markArea: feedQualityMarkArea(keys, flaggedKeys),
        },
      ],
    },
    { notMerge: true },
  )
}

/** Starts the chart once the container has a size; never at 0x0. */
function ensureChart(): boolean {
  if (chart.value) return true
  const el = container.value
  if (!el || el.clientWidth === 0 || el.clientHeight === 0) return false
  chart.value = echarts.init(el, undefined, { renderer: 'canvas' })
  return true
}

let observer: ResizeObserver | null = null

onMounted(() => {
  if (!container.value) return
  if (ensureChart()) render()
  observer = new ResizeObserver(() => {
    if (chart.value) chart.value.resize()
    else if (ensureChart()) render()
  })
  observer.observe(container.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  chart.value?.dispose()
  chart.value = null
})

watch(() => [props.buckets, props.grain, props.chart, props.flagged], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" role="img" aria-label="Active bindings over time" />
</template>
