<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart } from 'echarts/charts'
import { DataZoomComponent, GridComponent, LegendComponent, MarkAreaComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { CHECK_LABELS, type RiskChangeDay } from '@/api/risk'
import { fillCalendar, isMissing } from '@/lib/calendar'
import { chartColor, withAlpha } from '@/lib/chart-colors'
import { feedQualityMarkArea, feedQualityTooltip } from '@/lib/feed-quality-bands'
import { formatCompact, formatFull } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { tooltipBounds } from '@/lib/chart-tooltip'

echarts.use([BarChart, DataZoomComponent, GridComponent, LegendComponent, MarkAreaComponent, TooltipComponent, CanvasRenderer])

/**
 * Each day's SIM changes, split into what the risk lists count and what the screens set aside.
 *
 * One bar a day, two parts of the same total: counted changes in the series colour, set-aside ones
 * in neutral grey on top. Days the feed-quality monitor flagged are shaded, and days with no file
 * are gaps rather than joined over. Read together they show the September jump for what it is:
 * mostly set aside, and the rest still six to eight times an ordinary day - why numbers stay capped.
 */
const props = withDefaults(
  defineProps<{
    days: RiskChangeDay[]
    height?: string
    /**
     * Both (the default): one bar a day, counted and set aside stacked to the day's total. `changes`
     * alone is the total; `setAside` alone is only what the screens set aside.
     */
    series?: Array<'changes' | 'setAside'>
    /** Shade the days the feed-quality monitor flagged. */
    showFlagged?: boolean
    /** Where the zoom window opens, in percent of the days drawn. */
    zoomStart?: number
  }>(),
  { height: '18rem', series: () => ['changes', 'setAside'], showFlagged: true, zoomStart: 50 },
)

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

const flagged = computed(
  () => new Map(props.days.filter((d) => d.flagged.length).map((d) => [d.date, d.flagged.map((c) => CHECK_LABELS[c] ?? c).join(', ')])),
)

/** The bars for the chosen series; the shading rides on the first. */
function drawn(days: ReturnType<typeof fillCalendar<RiskChangeDay>>, dates: string[]) {
  const shading = props.showFlagged ? feedQualityMarkArea(dates, flagged.value) : undefined
  const both = props.series.includes('changes') && props.series.includes('setAside')
  const bars = [
    both
      ? { name: 'Counted', value: (d: RiskChangeDay) => d.changes - d.setAside, color: chartColor('--viz-1') }
      : props.series.includes('changes')
        ? { name: 'Numbers whose SIM changed', value: (d: RiskChangeDay) => d.changes, color: chartColor('--viz-1') }
        : null,
    props.series.includes('setAside')
      ? { name: 'Set aside as feed defects', value: (d: RiskChangeDay) => d.setAside, color: chartColor('--viz-null') }
      : null,
  ].filter((b) => b !== null)
  return bars.map((b, i) => ({
    name: b.name,
    type: 'bar',
    stack: 'day',
    data: days.map((d) => (isMissing(d) ? null : b.value(d))),
    itemStyle: { color: b.color, ...(i === bars.length - 1 ? { borderRadius: [4, 4, 0, 0] } : {}) },
    barMaxWidth: 14,
    markArea: i === 0 ? shading : undefined,
  }))
}

function render() {
  if (!chart.value) return

  const muted = chartColor('--c-text-muted')
  const border = chartColor('--c-border')
  const days = fillCalendar(props.days)
  const dates = days.map((d) => d.date)

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: chartColor('--c-text-secondary'), fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 32, bottom: 46 },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        backgroundColor: chartColor('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: chartColor('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const row = days[params[0]?.dataIndex ?? 0]
          if (!row) return ''
          if (isMissing(row)) return `<strong>${row.date}</strong><br/>no file - no data`
          return [
            `<strong>${row.date}</strong>`,
            `counted &nbsp; ${formatFull(row.changes - row.setAside)}`,
            `set aside &nbsp; ${formatFull(row.setAside)}`,
            `numbers whose SIM changed &nbsp; <strong>${formatFull(row.changes)}</strong>`,
          ].join('<br/>') + (props.showFlagged ? feedQualityTooltip(row.date, flagged.value) : '')
        },
      },
      xAxis: {
        type: 'category',
        data: dates,
        // The first and last dates align to the plot's edges: centred on the end ticks they ran off the canvas.
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true, alignMinLabel: 'left', alignMaxLabel: 'right' },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        name: 'numbers whose SIM changed',
        nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => formatCompact(v) },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      dataZoom: [
        { type: 'inside', start: props.zoomStart, end: 100 },
        {
          type: 'slider',
          start: props.zoomStart,
          end: 100,
          height: 18,
          bottom: 4,
          borderColor: border,
          fillerColor: withAlpha(chartColor('--c-accent'), 0.13),
          handleStyle: { color: chartColor('--c-accent') },
          textStyle: { color: muted, fontSize: 9 },
        },
      ],
      series: drawn(days, dates),
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

watch(() => [props.days, props.series, props.showFlagged], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" role="img" aria-label="SIM changes per day, counted and set aside" />
</template>
