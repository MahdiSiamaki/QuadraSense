<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { DataZoomComponent, GridComponent, LegendComponent, MarkAreaComponent, MarkLineComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { CHECK_LABELS, flaggedDays, type FeedQualityCheck, type FeedQualityDay, type FeedQualityResponse } from '@/api/quality'
import { fillCalendar, isMissing } from '@/lib/calendar'
import { chartColor } from '@/lib/chart-colors'
import { tooltipBounds } from '@/lib/chart-tooltip'
import { feedQualityMarkArea } from '@/lib/feed-quality-bands'
import { formatCompact, formatFull, formatPercent } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { CHECK_COLOURS, checkCount, checkRate, checkThreshold } from './feedTrend'

echarts.use([LineChart, DataZoomComponent, GridComponent, LegendComponent, MarkAreaComponent, MarkLineComponent, TooltipComponent, CanvasRenderer])

/**
 * The checks' daily rates or row counts on one axis - one unit either way - with flagged days shaded
 * and, for rates, each check's flagging line dashed in its own colour. A day with no file is a gap.
 */
const props = withDefaults(
  defineProps<{
    days: FeedQualityDay[]
    response: FeedQualityResponse | undefined
    checks: FeedQualityCheck[]
    measure: 'rate' | 'count'
    showReference: boolean
    height?: string
    zoomStart?: number
  }>(),
  { height: '18rem', zoomStart: 0 },
)

const container = ref<HTMLElement | null>(null)
const chart = shallowRef<echarts.ECharts | null>(null)
const { isDark } = useTheme()

function render() {
  if (!chart.value) return
  const muted = chartColor('--c-text-muted')
  const border = chartColor('--c-border')
  const days = fillCalendar(props.days)
  const dates = days.map((d) => d.date)
  const flagged = flaggedDays(props.response)
  const rate = props.measure === 'rate'

  const series = props.checks.map((check, i) => {
    const colour = chartColor(CHECK_COLOURS[check])
    const threshold = rate && props.showReference ? checkThreshold(props.response, check) : null
    return {
      name: CHECK_LABELS[check],
      type: 'line',
      data: days.map((d) => (isMissing(d) ? null : rate ? (checkRate(d, check) ?? null) : checkCount(d, check))),
      itemStyle: { color: colour },
      lineStyle: { width: 2 },
      symbol: 'none',
      markArea: i === 0 ? feedQualityMarkArea(dates, flagged) : undefined,
      markLine:
        threshold !== null
          ? {
              silent: true,
              symbol: 'none',
              lineStyle: { color: colour, type: 'dashed', width: 1 },
              label: { show: false },
              data: [{ yAxis: threshold }],
            }
          : undefined,
    }
  })

  chart.value.setOption(
    {
      animation: false,
      legend: { top: 0, right: 0, textStyle: { color: chartColor('--c-text-secondary'), fontSize: 11 }, itemWidth: 10, itemHeight: 10 },
      grid: { left: 8, right: 8, top: 30, bottom: 24 },
      tooltip: {
        ...tooltipBounds,
        trigger: 'axis',
        backgroundColor: chartColor('--c-surface-raised'),
        borderColor: border,
        textStyle: { color: chartColor('--c-text'), fontSize: 12 },
        formatter: (params: Array<{ dataIndex: number }>) => {
          const row = days[params[0]?.dataIndex ?? 0]
          if (!row) return ''
          if (isMissing(row)) return `<strong>${row.date}</strong><br/>not delivered - no data`
          const lines = props.checks.map((c) => {
            const finding = row.findings.find((f) => f.check === c)
            const value = rate ? (finding ? formatPercent(finding.rate * 100, 2) : '—') : `${formatFull(checkCount(row, c))} rows`
            return `${CHECK_LABELS[c]} &nbsp; ${value}${finding?.flagged ? ' <em>flagged</em>' : ''}`
          })
          return [`<strong>${row.date}</strong>`, `${formatFull(row.rows)} rows in the file`, ...lines].join('<br/>')
        },
      },
      xAxis: {
        type: 'category',
        data: dates,
        axisLabel: { color: muted, fontSize: 10, hideOverlap: true, alignMinLabel: 'left', alignMaxLabel: 'right' },
        axisLine: { lineStyle: { color: border } },
      },
      yAxis: {
        type: 'value',
        name: rate ? 'rate' : 'rows',
        nameTextStyle: { color: muted, fontSize: 10, align: 'left' },
        axisLabel: { color: muted, fontSize: 10, formatter: (v: number) => (rate ? formatPercent(v * 100, v < 0.01 ? 2 : 0) : formatCompact(v)) },
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

watch(() => [props.days, props.checks, props.measure, props.showReference, props.zoomStart], render, { deep: true })
watch(isDark, () => requestAnimationFrame(render))
</script>

<template>
  <div ref="container" :style="{ height }" class="w-full" role="img" :aria-label="`Feed-quality ${measure === 'rate' ? 'rates' : 'row counts'} per day`" />
</template>
